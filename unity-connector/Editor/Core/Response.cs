// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



namespace UnityCliConnector
{
    public class SuccessResponse
    {
        public bool success = true;
        public string message;
        public object data;

        public SuccessResponse(string message, object data = null)
        {
            this.message = message;
            this.data = data;
        }
    }

    /// <summary>
    /// Well-known errorKind values consumed by the CLI to map errors to
    /// exit codes. Keep in sync with internal/cli/exit/codes.go (FromKind).
    /// </summary>
    public static class ErrorKind
    {
        public const string Ambiguous = "ambiguous";
        public const string NotFound = "not_found";
        public const string Busy = "busy";
        public const string Usage = "usage";
        public const string Runtime = "runtime";
        public const string Unreachable = "unreachable";
    }

    public class ErrorResponse
    {
        public bool success = false;
        public string message;
        public string errorKind;
        public object data;

        public ErrorResponse(string message, object data = null)
        {
            this.message = message;
            this.data = data;
        }

        public ErrorResponse(string message, string errorKind, object data = null)
        {
            this.message = message;
            this.errorKind = errorKind;
            this.data = data;
        }

        // Convenience constructors for the common kinds.
        public static ErrorResponse Ambiguous(string message, object data = null)
            => new ErrorResponse(message, ErrorKind.Ambiguous, data);
        public static ErrorResponse NotFound(string message, object data = null)
            => new ErrorResponse(message, ErrorKind.NotFound, data);
        public static ErrorResponse Busy(string message, object data = null)
            => new ErrorResponse(message, ErrorKind.Busy, data);
        public static ErrorResponse Usage(string message, object data = null)
            => new ErrorResponse(message, ErrorKind.Usage, data);

        /// <summary>
        /// Build an ErrorResponse from a failed Result&lt;T&gt;, preserving its
        /// ErrorKind tag so the CLI can map it to the right exit code.
        /// </summary>
        public static ErrorResponse FromResult<T>(Result<T> result)
            => new ErrorResponse(result.ErrorMessage, result.ErrorKind);
    }
}
