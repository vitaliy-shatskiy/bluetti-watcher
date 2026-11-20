namespace BluettiWatcher.Modes;

/// <summary>
/// A module whose read is expensive (an NVIDIA profile export through the admin broker): the reconciler reads and writes its
/// resources when a mode is entered and when it is left, and keeps their status in between instead of reading at every
/// poll. A failed or unsettled resource is tried again on the next poll; a change made by hand meanwhile is seen on leaving.
/// </summary>
internal interface IChecksOnTransition;
