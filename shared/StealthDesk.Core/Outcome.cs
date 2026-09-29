namespace StealthDesk.Core;

/// <summary>Result of an operation that can be refused for an expected reason, without throwing.</summary>
public readonly record struct Outcome<T>(bool Succeeded, T? Value, string Error)
{
  public static implicit operator Outcome<T>(T value) => new(true, value, string.Empty);
}

public static class Outcome
{
  public static Outcome<T> Success<T>(T value) => new(true, value, string.Empty);

  public static Outcome<T> Failure<T>(string error) => new(false, default, error);
}
