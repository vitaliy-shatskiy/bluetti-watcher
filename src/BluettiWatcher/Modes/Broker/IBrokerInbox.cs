namespace BluettiWatcher.Modes.Broker;

/// <summary>The broker's files: the policy, the pending requests, the answers. The real one checks ACLs and links.</summary>
internal interface IBrokerInbox
{
    /// <summary>The policy file, or null when it is missing or cannot be read (then every request is refused).</summary>
    SecuredFile? ReadPolicy();

    /// <summary>The file names waiting in the requests folder (names only, nothing opened).</summary>
    IReadOnlyList<string> PendingRequests();

    /// <summary>Reads and deletes one request through one handle that never follows a link · null = could not be opened.</summary>
    SecuredFile? TakeRequest(string fileName);

    /// <summary>Writes the answer to a request (atomically: a reader never sees half of it).</summary>
    void WriteResponse(string id, string json);
}
