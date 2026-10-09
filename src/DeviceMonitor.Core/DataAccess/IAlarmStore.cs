using DeviceMonitor.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.DataAccess
{
    /// <summary>
    /// 报警记录的存储抽象。
    ///
    /// **为什么从 <see cref="IHistoryStore"/> 里拆出来**：
    ///   一开始把写/查报警直接加在 <c>IHistoryStore</c> 上，结果 <c>HistoryServiceTests</c> 里
    ///   那个只为"攒批规则"而存在的假 store **被迫实现了两个它根本用不到的方法** ——
    ///   接口一胖，实现者就被迫交"无关的税"（SOLID 里的接口隔离）。
    ///   拆开之后：样本侧只实现 <see cref="IHistoryStore"/>，报警侧只实现本接口，
    ///   而 <c>EfCoreHistoryStore</c> 作为真实实现**两个都实现**（同一个库、同一个写锁）。
    ///
    /// ★ 注意：DI 里这两个接口必须解析到**同一个 <c>EfCoreHistoryStore</c> 实例**。
    ///   各注册一个新实例的话，两边会各持一条连接、各有一把写锁 —— 锁不互斥，
    ///   样本写与报警写就会真的并发撞库。
    /// </summary>
    public interface IAlarmStore
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);

        Task<int> WriteAlarmAsync(IReadOnlyList<AlarmRecord> batch, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<AlarmRecord>> QueryAlarmAsync(string? deviceId, string? pointId, DateTime fromUtc, DateTime toUtc, int limit = 100_000, CancellationToken cancellationToken = default);
    }
}
