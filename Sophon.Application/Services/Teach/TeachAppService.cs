#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Core.Teach;

namespace Sophon.Application.Services.Teach
{
    /// <summary>
    /// 示教应用层管理服务。
    /// </summary>
    public class TeachAppService
    {
        private readonly TeachService _teachService;

        public TeachService TeachService => _teachService;

        public TeachAppService(TeachService teachService)
        {
            _teachService = teachService ?? throw new ArgumentNullException(nameof(teachService));
        }

        public IReadOnlyList<TeachPoint> GetAllPoints() => _teachService.Store.GetAllPoints();

        public IReadOnlyList<TeachPointGroup> GetAllGroups() => _teachService.Store.GetAllGroups();

        public TeachPoint CaptureCurrent(string name, string group = "Default", double speed = 20.0, string description = "")
        {
            return _teachService.CaptureCurrent(name, group, speed, description);
        }

        public void SavePoint(TeachPoint point)
        {
            _teachService.Store.SaveOrUpdatePoint(point);
        }

        public bool DeletePoint(string id)
        {
            return _teachService.Store.DeletePoint(id);
        }

        public void SaveGroup(TeachPointGroup group)
        {
            _teachService.Store.SaveOrUpdateGroup(group);
        }

        public bool DeleteGroup(string name)
        {
            return _teachService.Store.DeleteGroup(name);
        }

        public Task RunToPointAsync(TeachPoint point, double? overrideSpeed = null, CancellationToken ct = default)
        {
            return _teachService.RunToPointAsync(point, overrideSpeed, ct);
        }

        public Task RunGroupAsync(TeachPointGroup group, double? overrideSpeed = null, CancellationToken ct = default)
        {
            return _teachService.RunGroupAsync(group, overrideSpeed, ct);
        }
    }
}
