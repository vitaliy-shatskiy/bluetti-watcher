namespace BluettiWatcher.Modes.Broker;

/// <summary>The broker's verdict on one request file: the id to answer under (null = a file that names no request) and the result.</summary>
internal sealed record BrokerAnswer(string? Id, string Operation, BrokerResult Result);
