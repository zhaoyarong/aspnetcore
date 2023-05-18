``` ini

BenchmarkDotNet=v0.13.0, OS=Windows 10.0.22621
Intel Core i9-9900K CPU 3.60GHz (Coffee Lake), 1 CPU, 16 logical and 8 physical cores
.NET SDK=8.0.100-preview.5.23257.1
  [Host]     : .NET 8.0.0 (8.0.23.26801), X64 RyuJIT
  DefaultJob : .NET 8.0.0 (8.0.23.25213), X64 RyuJIT


```
|          Method |              Categories | PayloadLength |       Mean |     Error |    StdDev |    Gen 0 |    Gen 1 |    Gen 2 | Allocated |
|---------------- |------------------------ |-------------- |-----------:|----------:|----------:|---------:|---------:|---------:|----------:|
|  **OCS_StreamSync** | **Write_OutputCacheStream** |            **10** |   **1.264 μs** | **0.0152 μs** | **0.0135 μs** |   **0.3834** |   **0.0019** |        **-** |      **3 KB** |
| OCS_StreamAsync | Write_OutputCacheStream |            10 |   1.299 μs | 0.0076 μs | 0.0063 μs |   0.3834 |   0.0019 |        - |      3 KB |
| OCS_WriterAsync | Write_OutputCacheStream |            10 |   1.419 μs | 0.0224 μs | 0.0187 μs |   0.4292 |   0.0019 |        - |      4 KB |
|  **OCS_StreamSync** | **Write_OutputCacheStream** |          **1000** |   **1.791 μs** | **0.0083 μs** | **0.0065 μs** |   **1.3123** |   **0.0210** |        **-** |     **11 KB** |
| OCS_StreamAsync | Write_OutputCacheStream |          1000 |   1.840 μs | 0.0365 μs | 0.0448 μs |   1.3142 |   0.0210 |        - |     11 KB |
| OCS_WriterAsync | Write_OutputCacheStream |          1000 |   1.901 μs | 0.0141 μs | 0.0125 μs |   1.2131 |   0.0210 |        - |     10 KB |
|  **OCS_StreamSync** | **Write_OutputCacheStream** |         **65553** |  **98.853 μs** | **0.8037 μs** | **0.7517 μs** |  **65.0635** |  **65.0635** |  **65.0635** |    **517 KB** |
| OCS_StreamAsync | Write_OutputCacheStream |         65553 |  97.956 μs | 0.7726 μs | 0.7227 μs |  65.1855 |  65.1855 |  65.1855 |    518 KB |
| OCS_WriterAsync | Write_OutputCacheStream |         65553 | 113.790 μs | 0.7721 μs | 0.7222 μs |  79.7119 |  79.7119 |  79.7119 |    562 KB |
|  **OCS_StreamSync** | **Write_OutputCacheStream** |        **262161** | **163.657 μs** | **1.0428 μs** | **0.8708 μs** | **268.3105** | **233.3984** | **212.1582** |  **1,308 KB** |
| OCS_StreamAsync | Write_OutputCacheStream |        262161 | 155.656 μs | 1.5528 μs | 1.3765 μs | 264.8926 | 229.7363 | 208.7402 |  1,306 KB |
| OCS_WriterAsync | Write_OutputCacheStream |        262161 | 168.101 μs | 1.1700 μs | 1.0372 μs | 271.2402 | 239.2578 | 214.3555 |  1,322 KB |
|                 |                         |               |            |           |           |          |          |          |           |
|       **ReadAsync** |                    **Read** |            **10** |   **1.608 μs** | **0.0254 μs** | **0.0237 μs** |   **0.4272** |   **0.0038** |        **-** |      **3 KB** |
|       **ReadAsync** |                    **Read** |          **1000** |   **1.628 μs** | **0.0231 μs** | **0.0217 μs** |   **0.5436** |   **0.0057** |        **-** |      **4 KB** |
|       **ReadAsync** |                    **Read** |         **65553** |   **4.935 μs** | **0.0496 μs** | **0.0464 μs** |   **8.1940** |   **1.6327** |        **-** |     **68 KB** |
|       **ReadAsync** |                    **Read** |        **262161** |  **17.414 μs** | **0.3404 μs** | **0.3496 μs** |  **31.5552** |  **10.4980** |        **-** |    **260 KB** |
