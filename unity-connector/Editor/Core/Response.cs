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

    public class ErrorResponse
    {
        public bool success = false;
        public string message;
        public object data;

        public ErrorResponse(string message, object data = null)
        {
            this.message = message;
            this.data = data;
        }
    }
}
