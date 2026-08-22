```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12800H 2.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method       | Mean        | Error       | StdDev    | Gen0   | Allocated |
|------------- |------------:|------------:|----------:|-------:|----------:|
| DispatchTool | 153.4679 ns | 118.3133 ns | 6.4851 ns | 0.0088 |     112 B |
| SchemaAccess |   0.3925 ns |   0.7109 ns | 0.0390 ns |      - |         - |
