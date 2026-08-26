using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Web
{
    public static class DingoCmsEditorWebUi
    {
        private const string RESOURCE_PATH = "DingoCmsEditorServer/index";

        public static string LoadHtml()
        {
            var asset = Resources.Load<TextAsset>(RESOURCE_PATH);
            return asset != null
                ? asset.text
                : "<!doctype html><html><body><h1>DingoCMS Editor Server</h1>"
                  + "<p>The embedded Web UI resource is missing.</p></body></html>";
        }
    }
}
