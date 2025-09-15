# Summary
A Blazor WASM web app that approximates the most profitable planting schedule in Stardew Valley, based on available gold, field tiles, crops, starting day, and season length. Applicable to a number of farming games: Harvest Moon, Story of Seasons, Harvestella, Fields of Mistria, Rune Factory, etc.

The algorithm is a heuristic decision tree simulation, with caching and bucketing optimizations.

There are millions of possible schedules, so the heuristic is key to reducing the state space and finding an approximate solution within the bounds of a web browser tab's limited resources. Since this is a WebAssembly web app, Chrome only gives it 2 GB of memory, and a single thread (instead of Javascript's 4 GB). iOS Safari only gives a tab 1-1.5 GB.

# Deployment

Deploying simply requires clicking the Publish button in Visual Studio. In more detail:
```
1. Right-click the CropPlanner project in Visual Studio and select Publish.
2. Select "crop-planner-prod.pubxml" for the production site or "-preview.pubxml" for the preview site.
3. Click the "Publish" button.
```

Deployment can also be performed with the CLI command "swa".