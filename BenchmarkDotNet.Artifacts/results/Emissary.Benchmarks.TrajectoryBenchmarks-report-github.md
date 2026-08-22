```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12800H 2.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method             | Mean      | Error    | StdDev    | Gen0   | Allocated |
|------------------- |----------:|---------:|----------:|-------:|----------:|
| ReplayToolLoopRun  |  4.206 μs | 2.116 μs | 0.1160 μs | 0.4425 |   5.42 KB |
| SerializeRoundTrip | 11.999 μs | 4.290 μs | 0.2352 μs | 0.9766 |  12.13 KB |
| Deserialize        |  8.323 μs | 2.669 μs | 0.1463 μs | 0.4730 |   5.88 KB |
