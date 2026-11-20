namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// A file the broker read, with the facts its trust depends on: who owns it, whether it is a plain file (no reparse point,
/// one hard link) and which accounts other than Administrators, SYSTEM and TrustedInstaller may write it.
/// </summary>
internal sealed record SecuredFile(string Text, string OwnerSid, bool Plain, IReadOnlyList<string> OtherWriters);
