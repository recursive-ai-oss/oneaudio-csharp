namespace OneAudio;

/// <summary>
/// Base class of every error the library raises about audio data. Other exceptions leaving public methods are
/// argument exceptions, <see cref="NotSupportedException"/> for unavailable capabilities, <see cref="ObjectDisposedException"/>,
/// I/O exceptions from the underlying stream, and <see cref="OperationCanceledException"/>.
/// </summary>
public abstract class OneAudioException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    protected OneAudioException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The underlying cause.</param>
    protected OneAudioException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The input is not valid for its format: truncated headers, bad magic numbers, inconsistent fields.</summary>
public class AudioFormatException : OneAudioException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the input.</param>
    public AudioFormatException(string message) : base(message) { }
}

/// <summary>The input is valid (or plausibly valid) but uses a feature or format this build does not support, or no registered handler recognises it.</summary>
public class UnsupportedAudioFormatException : AudioFormatException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is unsupported.</param>
    public UnsupportedAudioFormatException(string message) : base(message) { }
}

/// <summary>An input asks for more than the configured <see cref="AudioLimits"/> allow. Raised before the allocation the limit guards.</summary>
public sealed class AudioLimitExceededException : OneAudioException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="limitName">Name of the <see cref="AudioLimits"/> property that was exceeded.</param>
    /// <param name="limit">The configured maximum.</param>
    /// <param name="requested">The value the input asked for.</param>
    public AudioLimitExceededException(string limitName, long limit, long requested)
        : base($"{limitName} limit exceeded: input needs {requested}, limit is {limit}.")
    {
        LimitName = limitName;
        Limit = limit;
        Requested = requested;
    }

    /// <summary>Name of the exceeded <see cref="AudioLimits"/> property.</summary>
    public string LimitName { get; }

    /// <summary>The configured maximum.</summary>
    public long Limit { get; }

    /// <summary>The value the input asked for.</summary>
    public long Requested { get; }
}
