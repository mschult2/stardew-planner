using StardewCropCalculatorLibrary;
using BlazorWorker.BackgroundServiceFactory;
using BlazorWorker.Core;
using BlazorWorker.WorkerBackgroundService;
using System.Text;
using System.Text.Json;
using static StardewCropCalculatorLibrary.GameStateCalendarFactory;
using System.Threading.Channels;

namespace CropPlanner
{
    /// <summary>
    /// Service that processes calendar data in the background.
    /// </summary>
    public class CalendarProcessorService(IWorkerFactory workerFactory, IHardwareInfo hardwareInfo) : ICalendarProcessor, IAsyncDisposable
    {
        private static readonly Yielder Yielder = new();

        private int workerCount = 4;
        private IWorker[] workers;
        private IWorkerBackgroundService<CalendarProcessor>[] services;
        private bool initialized = false;
        private bool initializing = false;

        private Dictionary<string, Crop> cropMap;
        private int numDays;

        private Queue<int> availableServices;
        private Channel<int> availableServicesChannel;
        private Dictionary<int, IWorkerBackgroundService<CalendarProcessor>> busyServices;
        private readonly Dictionary<string, (TimeSpan Time, int WorkerId, string Details)> workerRunTime = [];

        public async Task InitializeAsync()
        {
            try
            {
                if (!initialized && !initializing)
                {
                    initializing = true;

                    workerCount = await hardwareInfo.GetThreadCapacity();

                    // Create workers (takes a few seconds)
                    workers = await Task.WhenAll(
                        Enumerable.Range(0, workerCount).Select(_ => workerFactory.CreateAsync())
                    );

                    services = await Task.WhenAll(
                        workers.Select(w => w.CreateBackgroundServiceAsync<CalendarProcessor>())
                    );

                    availableServices = [];
                    availableServicesChannel = Channel.CreateUnbounded<int>();
                    for (int i = 0; i < workerCount; ++i)
                    {
                        availableServices.Enqueue(i);
                        availableServicesChannel.Writer.TryWrite(i);
                    }

                    services.Select((svc, i) => i);

                    busyServices = [];

                    // Force workers to initialize (takes a few seconds)
                    for (int i = 0; i < workerCount; ++i)
                        await services[i].RunAsync(processor => processor.Warmup($"{i}"));

                    initialized = true;
                    initializing = false;

                    Console.WriteLine($"[CalendarProcessorService.Initialize] Created {services.Length} workers");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CalendarProcessorService] ERROR: {e.Message}");

                initialized = false;
                initializing = false;
            }
        }

        public async Task ConfigureAsync(double startingGold, int startingTiles, int numDays, double cheapestCropBuyPrice, IEnumerable<Crop> crops)
        {
            if (!(await EnsureInitializedAsync()))
                return;

            this.numDays = numDays;
            this.cropMap = crops.ToDictionary(c => c.name);
            var serCrops = Crop.SerializeCrops(crops);

            foreach (var service in services)
                await service.RunAsync(processor => processor.Configure(startingGold, startingTiles, numDays, cheapestCropBuyPrice, serCrops));
        }

        /// <summary> Process each input node, returning its direct children. </summary>
        public async Task<IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_Shallow(IEnumerable<(int Day, string SerializedCalendar)> inputNodes)
        {
            if (!(await EnsureInitializedAsync()))
                return null;

            // Materialize so we can index/slice
            var nodeList = inputNodes.ToList();
            int n = nodeList.Count;

            if (n == 0)
                return [];

            int serviceCount = services.Length;
            int chunkSize = (int)Math.Ceiling((double)n / serviceCount);

            var tasks = new List<Task<List<(int InputIndex, int Day, string SerializedCalendar)>>>();
            var offsets = new List<int>(serviceCount); // start index of each chunk

            for (int serviceIndex = 0; serviceIndex < serviceCount; ++serviceIndex)
            {
                if (Yielder.CheckMemoryLimit())
                    return null;

                // Yield back to renderer so UI doesn't freeze
                await Yielder.Yield();

                int start = serviceIndex * chunkSize;
                if (start >= n) break; // no more items

                var nodesChunk = nodeList.Skip(start).Take(Math.Min(chunkSize, n - start)).ToList();
                if (nodesChunk.Count == 0) break;

                var days = nodesChunk.Select(x => x.Day).ToList();
                var cals = nodesChunk.Select(x => x.SerializedCalendar).ToList();

                string daysJson = JsonSerializer.Serialize(days);
                string calsJson = JsonSerializer.Serialize(cals);

                // remember the global starting index for this chunk
                offsets.Add(start);

                // Process
                tasks.Add(services[serviceIndex].RunAsync(p => p.ProcessNodes(daysJson, calsJson)));
            }

            var results = await Task.WhenAll(tasks);

            // Remap each worker's local InputIndex by adding its chunk's start offset, then flatten.
            var serOutputNodes = new List<(int InputIndex, int Day, string SerializedCalendar)>();
            serOutputNodes.EnsureCapacity(results.Sum(r => r.Count));

            for (int i = 0; i < results.Length; i++)
            {
                int offset = offsets[i];
                foreach (var (InputIndex, Day, SerializedCalendar) in results[i])
                {
                    // Yield back to renderer so UI doesn't freeze
                    await Yielder.Yield();

                    serOutputNodes.Add((InputIndex + offset, Day, SerializedCalendar));
                }
            }

            // Deserialize
            return serOutputNodes.Select(x => (x.InputIndex, new GetMostProfitableCropArgs(x.Day, new GameStateCalendar(numDays, cropMap, x.SerializedCalendar))));
        }

        public async Task<IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_Deep(IEnumerable<GetMostProfitableCropArgs> _inputNodes)
        {
            if (!(await EnsureInitializedAsync()))
                return null;

            LastNodesProcessed = 0;
            LastCacheHitsProcessed = 0;

            // Longest Processing Time (LPT):
            // Some nodes take 0.5s and other 10s due to having bigger subtrees.
            // So it's essential to run the slow ones first, else the last few operations will take a long time without being parallelized across all workers.

            IEnumerable<(int InputIndex, GetMostProfitableCropArgs Node)> inputNodes = _inputNodes
                .Select((s, i) => (Node: s, Key: EstimateCost(s.Calendar), InputIndex: i))
                .OrderByDescending(x => x.Key.PrimaryCost)
                .ThenByDescending(x => x.Key.SecondaryCost)
                .Select(x => (x.InputIndex, x.Node));

            // Serialize input nodes
            IEnumerable<(int InputIndex, int Day, string SerCal)> serInputNodes = inputNodes
                .Select(node => (node.InputIndex, node.Node.Day, SerializeGameStateCalendar(node.Node.Calendar, node.Node.Day)));

            List<Task<List<(int InputIndex, GetMostProfitableCropArgs Node)>>> tasks = [];

            foreach (var serInputNode in serInputNodes)
            {
                if (Yielder.CheckMemoryLimit())
                    return null;

                // Yield back to renderer so UI doesn't freeze
                // (Just a precaution, since this loop typically waits for long periods of time)
                await Yielder.Yield();

                // Wait until worker is free
                int serviceId = await availableServicesChannel.Reader.ReadAsync();
                busyServices[serviceId] = services[serviceId];

                // Fire off worker without waiting
                tasks.Add(ProcessAsync_Deep_Single((serInputNode.Day, serInputNode.SerCal), serInputNode.InputIndex, serviceId));
            }

            // Skip the Load Balancer:
            // If the last few operations are long, Short-circuiting and rebalancing them across all workers seems like a good idea in theory.
            // But in reality, serializing and marshalling 1000 next-nodes instead of one leaf node takes up as much time as we'd be saving.

            // Wait for all workers to complete
            var result = await Task.WhenAll(tasks);

            return result.SelectMany(x => x);
        }

        private async Task<List<(int InputIndex, GetMostProfitableCropArgs Node)>> ProcessAsync_Deep_Single((int Day, string SerializedCalendar) serInputNode, int inputIndex, int serviceId)
        {
            int curNodesProcessed = 0;
            int curCacheHits = 0;

            System.Diagnostics.Stopwatch debugStopwatch = null;
            string debugGuid = null;
            GameState debugGameState = null;
            if (EnableStats)
            {
                debugGuid = Guid.NewGuid().ToString();
                debugStopwatch = System.Diagnostics.Stopwatch.StartNew();
                debugGameState = (new GameStateCalendar(numDays, cropMap, serInputNode.SerializedCalendar)).GameStates[serInputNode.Day];
            }

            IEnumerable<(int Day, string SerCal)> serOutputNodes;

            try
            {
                // Process input node
                (serOutputNodes, curNodesProcessed, curCacheHits) = await services[serviceId].RunAsync(p => p.ProcessNodes_Deep(serInputNode.Day, serInputNode.SerializedCalendar));
                Yielder.OperationCount = LastNodesProcessed += curNodesProcessed;
                Yielder.CacheHitCount = LastCacheHitsProcessed += curCacheHits;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CalendarProcessorService.ProcessAsync_Deep_Single] ERROR: ProcessNodes_Deep failed: {e.Message}");
                serOutputNodes = [];
            }
            finally
            {
                // Return worker to pool
                availableServicesChannel.Writer.TryWrite(serviceId);
                busyServices.Remove(serviceId);
            }

            if (EnableStats)
                workerRunTime[debugGuid] = (debugStopwatch.Elapsed, serviceId, $"Day {serInputNode.Day}, nodes: {curNodesProcessed}, available gold: {debugGameState.Wallet}, available tiles: {debugGameState.FreeTiles}");

            // Deserialize output nodes
            var outputNodes = new List<(int InputIndex, GetMostProfitableCropArgs Node)>();

            foreach (var (day, serCal) in serOutputNodes)
            {
                if (serCal == null) continue;
                outputNodes.Add((inputIndex, new GetMostProfitableCropArgs(day, new GameStateCalendar(numDays, cropMap, serCal))));
            }

            return outputNodes;
        }

        private (int PrimaryCost, int SecondaryCost) EstimateCost(GameStateCalendar cal)
        {
            var harvestDays = cal.GameStates.Where(kv => kv.Value.DayOfInterest).Select(kv => kv.Key);
            int h = harvestDays.Count();
            int early = harvestDays.Sum(d => numDays - d + 1);
            return (h, early);
        }

        private async Task<bool> EnsureInitializedAsync()
        {
            while (initializing)
                await Task.Delay(1);

            if (initialized)
                return true;
            else
                return false;
        }

        // For some reason, Blazor WASM isn't disposing DI objects or components. Even if they implement IAsyncDisposable or IDisposable.
        public async ValueTask DisposeAsync()
        {
            Console.WriteLine("[CalendarProcessorService] Disposing CalendarProcessorService.");

            try
            {
                availableServices.Clear();
                availableServices = null;
                busyServices.Clear();
                busyServices = null;
                availableServicesChannel.Writer.TryComplete();

                var disposeTasks = new List<Task>(capacity: workerCount);

                if (workers is not null)
                {
                    foreach (var worker in workers)
                        disposeTasks.Add(worker.DisposeAsync().AsTask());

                    workers = null;
                }

                if (disposeTasks.Count > 0)
                    await Task.WhenAll(disposeTasks);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CalendarProcessorService] ERROR: failed to dispose CalendarProcessorService: {e.Message}");
            }
        }

        public int LastNodesProcessed { get; private set; }

        public int LastCacheHitsProcessed { get; private set; }

        public bool EnableStats { get; set; }

        public void PrintStats()
        {
            if (EnableStats)
            {
                foreach (var pair in workerRunTime.OrderByDescending(x => x.Value))
                    Console.WriteLine($"[CalendarProcessorService][Worker {pair.Value.WorkerId}] Worker run {pair.Key}: {pair.Value.Time:mm\\:ss\\.ff}, {pair.Value.Details}");

                workerRunTime.Clear();
            }
        }

        /// <summary>
        /// Processes calendar data. Uses serialized input/output.
        /// </summary>
        private class CalendarProcessor
        {
            private static readonly bool UseReturnSignal = false;
            private static readonly bool UseCache = true;

            private readonly Yielder yielder = new(100);
            private string id = Guid.NewGuid().ToString();
            private double cheapestCropBuyPrice;
            private IEnumerable<Crop> crops;
            private Dictionary<string, Crop> cropMap;
            private double startingGold;
            int startingTiles;
            private int numDays;

            private readonly Queue<GetMostProfitableCropArgs> daysToEvaluate = new();
            private double bestWealth = 0;
            private GameStateCalendar bestCalendar = null;

            private int returnSignal = 0;

            private readonly HashSet<string> nodeCache = [];

            /// <summary> Blazor workers are lazy-initialized which takes seconds, so do it ahead of time. </summary>
            public void Warmup(string id)
            {
                this.id = id;
            }

            /// <summary>
            /// Request cancellation.
            /// </summary>
            public void Cancel()
            {
                Interlocked.Exchange(ref returnSignal, 1);
            }

            public void Configure(double startingGold, int startingTiles, int numDays, double cheapestCropBuyPrice, string serCrops)
            {
                this.startingGold = startingGold;
                this.startingTiles = startingTiles;
                this.numDays = numDays;
                this.cheapestCropBuyPrice = cheapestCropBuyPrice;
                this.crops = Crop.DeserializeCrops(serCrops);
                this.cropMap = crops.ToDictionary(c => c.name);
                nodeCache.Clear();
            }

            public List<(int Day, string SerializedCalendar)> ProcessNode(int day, string serializedInputCalendar)
            {
                // Process node
                var newDaysToEvaluate = GameStateCalendarFactory.ProcessNode(day, serializedInputCalendar,
                    cheapestCropBuyPrice, startingGold, startingTiles, crops, numDays);

                // Serialize output
                var serializedNodes = new List<(int Day, string SerializedCalendar)>(newDaysToEvaluate.Count);
                foreach (var newDayToEval in newDaysToEvaluate)
                {
                    string serializedCalendar = SerializeGameStateCalendar(newDayToEval.Calendar, day, includePlants: true, round: false);
                    serializedNodes.Add((newDayToEval.Day, serializedCalendar));
                }

                return serializedNodes;
            }

            public List<(int InputIndex, int Day, string SerializedCalendar)> ProcessNodes(string daysJson, string serializedCalendarsJson)
            {
                var days = JsonSerializer.Deserialize<List<int>>(daysJson);
                var serializedCalendars = JsonSerializer.Deserialize<List<string>>(serializedCalendarsJson);

                var serializedNodes = new List<(int InputIndex, int Day, string SerializedCalendar)>();

                for (int i = 0; i < days.Count; ++i)
                {
                    // Check cache if input node has been explored before
                    if (UseCache && nodeCache.Contains(serializedCalendars[i]))
                        continue;

                    var outputNodes = ProcessNode(days[i], serializedCalendars[i]);

                    // Mark output node in cache
                    if (UseCache && serializedCalendars[i] != null && !nodeCache.Contains(serializedCalendars[i]))
                        nodeCache.Add(serializedCalendars[i]);

                    foreach (var (Day, SerializedCalendar) in outputNodes)
                        serializedNodes.Add((i, Day, SerializedCalendar));
                }

                return serializedNodes;
            }

            public async Task<(IEnumerable<(int Day, string SerCal)>, int ProcCount, int cacheCount)> ProcessNodes_Deep(int day, string serializedCalendar)
            {
                // Signal reset
                if (UseReturnSignal)
                    Interlocked.Exchange(ref returnSignal, 0);

                daysToEvaluate.Clear();
                var startNode = new GetMostProfitableCropArgs(day, new GameStateCalendar(numDays, cropMap, serializedCalendar));
                bestWealth = startNode.Calendar.Wealth;
                bestCalendar = null;
                int nodesProcessedStat = 0;
                int cacheHitsStat = 0;

                daysToEvaluate.Enqueue(startNode);

                while (daysToEvaluate.Count > 0)
                {
                    ++nodesProcessedStat;

                    // Signal raised
                    if (UseReturnSignal)
                    {
                        await yielder.Yield();

                        if (Interlocked.CompareExchange(ref returnSignal, 0, 1) == 1)
                            return (daysToEvaluate.Select(node => (node.Day, SerializeGameStateCalendar(node.Calendar, day, includePlants: true, round: false))), nodesProcessedStat, cacheHitsStat);
                    }

                    // Serialize input node
                    var inputNode = daysToEvaluate.Dequeue();
                    string serInputNode = SerializeGameStateCalendar(inputNode.Calendar, inputNode.Day);

                    // Check cache if input node has been explored before
                    if (UseCache && nodeCache.Contains(serInputNode))
                    {
                        ++cacheHitsStat;
                        continue;
                    }

                    // Process input node
                    var outputNodes = GameStateCalendarFactory.ProcessNode(inputNode.Day, serInputNode,
                        cheapestCropBuyPrice, startingGold, startingTiles, crops, numDays);

                    // Mark output node in cache
                    if (UseCache && serInputNode != null && !nodeCache.Contains(serInputNode))
                        nodeCache.Add(serInputNode);

                    // Save output nodes.
                    foreach (var outputNode in outputNodes)
                    {
                        // Merge output with input.
                        if (inputNode.Day > 1)
                            outputNode.Calendar.Merge(inputNode.Calendar, startingDay: day, endingDay: inputNode.Day - 1, deep: false);

                        if (outputNode.Calendar.Wealth > bestWealth)
                        {
                            bestWealth = outputNode.Calendar.Wealth;
                            bestCalendar = outputNode.Calendar;
                        }

                        if (outputNode.Day < numDays)
                            daysToEvaluate.Enqueue(outputNode);
                    }
                }

                // Return best node
                // (Don't round output, only needed for input caching)
                string resultCalendar = bestCalendar != null ? SerializeGameStateCalendar(bestCalendar, day, includePlants: true, round: false) : null;
                return ([(numDays + 1, resultCalendar)], nodesProcessedStat, cacheHitsStat);
            }

            public override string ToString()
            {
                var sb = new StringBuilder();

                sb.AppendLine($"\n[CalendarProcessingService.Configure] startingGold: {startingGold}, startingTiles: {startingTiles}, numDays: {numDays}, cheapestCropBuyPrice: {cheapestCropBuyPrice}g\n");
                foreach (var crop in crops)
                    sb.AppendLine($"[CalendarProcessingService.Configure] {crop.name}: maturity {crop.timeToMaturity}, buy {crop.buyPrice}, sell {crop.sellPrice}");

                return sb.ToString();
            }
        }
    }
}
