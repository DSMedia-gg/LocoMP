using LocoMP.Core.Protocol;

// Headless dedicated server — skeleton. Proves the Core stack composes and runs on modern .NET with
// no game install (03 §6). The real UDP server + console admin land in M6 (07 §M6).
Console.WriteLine($"LocoMP.Server (skeleton) — protocol v{ProtocolVersion.Current}.");
Console.WriteLine("Dedicated server arrives in M6. Nothing to serve yet.");
return 0;
