A Blazor web app that approximates the most profitable planting schedule in Stardew Valley, based on available gold, field tiles, crops, and starting day.

The algorithm is a heuristic decision tree simulation, with caching and bucketing optimizations.

There are millions of possible schedules, so the heuristic is essential to reducing the state space and finding an approximate solution within the bounds of a web browser tab's limited resources. Since this is a WebAssembly web app, it gets only 2 GB of memory in Chrome and a single thread. However, some browsers are even more limited. I Think Safari mobile only provides a few hundred MB.
