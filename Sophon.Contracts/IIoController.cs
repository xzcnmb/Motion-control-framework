using System;
using System.Collections.Generic;

namespace Sophon.Contracts
{
    /// <summary>
    /// IO 控制接口。所有 IO 一律经【虚拟点名】访问（解耦厂商"卡号+位号"平面），
    /// 由驱动适配器完成 点名→物理位 映射（真卡 DI 平面、限位 DI 映射都依赖点名）。
    /// </summary>
    public interface IIoController
    {
        /// <summary>读数字输入点。</summary>
        bool ReadDi(string pointName);

        /// <summary>写数字输出点。</summary>
        void WriteDo(string pointName, bool value);

        /// <summary>全部 DI 快照（点名→值）。</summary>
        IReadOnlyDictionary<string, bool> SnapshotDi();

        /// <summary>全部 DO 快照（点名→值）。</summary>
        IReadOnlyDictionary<string, bool> SnapshotDo();

        /// <summary>DI 变化事件（点名→新值）。</summary>
        event Action<string, bool>? DiChanged;

        /// <summary>已声明的 DI 点名清单。</summary>
        IReadOnlyList<string> DiPointNames { get; }

        /// <summary>已声明的 DO 点名清单。</summary>
        IReadOnlyList<string> DoPointNames { get; }
    }
}