# YSharp.Server.Testing

The storage conformance suite for [YSharp.Server](https://www.nuget.org/packages/YSharp.Server). If you write an `IUpdateStore` backend, for example over a database table or files, subclass `UpdateStoreConformance` in your test project and run its checks. They cover sequence numbers, ordering, compaction, deletion, crashes during an append or a compaction, torn tails, mid-log corruption and concurrency.

The package has no test framework dependency: each check reports a `ConformanceResult`, so it runs under xUnit, NUnit, MSTest or a console program.

- [Storage in the server guide](https://github.com/jcannon98188/y-sharp/blob/main/docs/server.md#storage)

MIT licensed.
