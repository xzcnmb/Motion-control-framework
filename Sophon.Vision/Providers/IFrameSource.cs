using OpenCvSharp;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 图像源抽象接口。支持测试内存图、文件夹轮询、工业相机SDK等。
    /// </summary>
    public interface IFrameSource : IDisposable
    {
        /// <summary>
        /// 尝试抓取或获取当前最新图像帧。
        /// </summary>
        /// <param name="frame">抓取到的 Mat 对象（由调用方释放或管理）</param>
        /// <returns>若成功获取则返回 true，否则 false</returns>
        bool TryGrab(out Mat? frame);
    }
}
