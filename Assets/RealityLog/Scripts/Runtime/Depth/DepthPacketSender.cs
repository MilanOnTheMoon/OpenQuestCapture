using System.Text;
using UnityEngine;
using Meta.Net.NativeWebSocket;
using Newtonsoft.Json;

public class DepthPacketSender : MonoBehaviour
{
    [SerializeField] private string websocketUrl = "ws://192.168.1.2:5001/ws/depth";
    [SerializeField] private bool logJsonLengthOnly = true;

    private WebSocket websocket;
    private bool isConnected = false;

    private async void Start()
    {
        websocket = new WebSocket(websocketUrl);
        Debug.Log("[DepthSender] Start() running. Connecting to: " + websocketUrl);
        websocket.OnOpen += () =>
        {
            isConnected = true;
            Debug.Log("[DepthSender] WebSocket connected.");
        };

        websocket.OnError += (errorMsg) =>
        {
            Debug.LogError("[DepthSender] WebSocket error: " + errorMsg);
        };

        websocket.OnClose += (closeCode) =>
        {
            isConnected = false;
            Debug.LogWarning("[DepthSender] WebSocket closed.");
        };

        websocket.OnMessage += (bytes, offset, length) =>
        {
            string msg = Encoding.UTF8.GetString(bytes, offset, length);
            Debug.Log("[DepthSender] Server message: " + msg);
        };

        await websocket.Connect();
    }

    public async void SendPacket(DepthPacket packet)
    {
        if (!isConnected || websocket == null || websocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("[DepthSender] WebSocket not connected. Packet not sent.");
            return;
        }

        string json = JsonConvert.SerializeObject(packet);

        if (logJsonLengthOnly)
        {
            Debug.Log($"[DepthSender] Packet JSON length: {json.Length}");
        }

        await websocket.SendText(json);
    }

    private async void OnApplicationQuit()
    {
        if (websocket != null)
        {
            await websocket.Close();
        }
    }
}