using System.Runtime.CompilerServices;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Tests;

/// <summary>Some tests open SQLite connections of their own, before any StationStore: the winsqlite3 provider is set first.</summary>
internal static class SqliteModuleInit
{
    [ModuleInitializer]
    internal static void Init() => SqliteSetup.Init();
}
