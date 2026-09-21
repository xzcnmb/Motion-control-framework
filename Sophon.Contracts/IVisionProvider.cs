using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Contracts
{
    /// <summary>
    /// 视觉提供者接口：触发式测量 + 实时取景。不绑定任何厂商 SDK。
    /// Sim 与 OpenCvSharp/海康等均为实现方。
    /// </summary>
    public interface IVisionProvider : IDisposable
    {
        /// <summary>可用相机 ID 清单。</summary>
        IReadOnlyList<string> Cameras { get; }

        /// <summary>建立连接。</summary>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>断开连接。</summary>
        Task DisconnectAsync();

        /// <summary>
        /// 触发一次采集与测量，返回 requestId；结果经 ResultReady 事件回报。
        /// </summary>
        Guid Trigger(string cameraId);

        /// <summary>测量结果事件（与请求 id 关联）。</summary>
        event Action<VisionResult>? ResultReady;

        // ---------- 实时取景 ----------
        void StartLive(string cameraId);
        void StopLive();

        /// <summary>实时帧事件（灰度、行优先）。</summary>
        event Action<VisionFrame>? FrameReady;
    }
}