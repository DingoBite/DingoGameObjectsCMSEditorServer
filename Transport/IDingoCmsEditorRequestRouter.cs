#if NEWTONSOFT_EXISTS
using System;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Transport
{
    public interface IDingoCmsEditorRequestRouter
    {
        string WebIndexHtml { get; }

        JArray DescribeTools();
        JObject Execute(string operation, JObject arguments);
    }

    public sealed class DingoCmsEditorRequestException : Exception
    {
        public string Code { get; }
        public JObject Details { get; }

        public DingoCmsEditorRequestException(
            string code,
            string message,
            JObject details = null)
            : base(message)
        {
            Code = string.IsNullOrWhiteSpace(code)
                ? "operation_failed"
                : code;
            Details = details == null
                ? null
                : (JObject)details.DeepClone();
        }
    }
}
#endif
