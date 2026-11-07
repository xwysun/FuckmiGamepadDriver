using System;
using System.Diagnostics;
using System.IO;
using Nefarius.Drivers.HidHide;

namespace MiGamepad.Core.Cloaking
{
    /// <summary>
    /// 管理 HidHide 驱动过滤规则，自动将本程序加入白名单并隐藏物理小米手柄
    /// </summary>
    public class HidHideManager
    {
        private readonly HidHideControlService? _service;
        private readonly bool _isAvailable;

        public bool IsAvailable => _isAvailable;
        public bool IsActive => _isAvailable && (_service?.IsActive ?? false);

        public HidHideManager()
        {
            try
            {
                _service = new HidHideControlService();

                // 检查驱动是否可用（支持通过句柄响应 IOCTL 读取 IsActive）
                bool operational = false;
                try
                {
                    operational = _service.IsInstalled || _service.IsOperational || _service.IsActive;
                }
                catch
                {
                    operational = false;
                }

                if (operational)
                {
                    _isAvailable = true;
                    ConfigureCurrentProcessWhitelist();
                }
            }
            catch (Exception ex)
            {
                _isAvailable = false;
                Debug.WriteLine($"[HidHideManager] HidHide 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将本程序的完整可执行文件路径加入 HidHide 白名单
        /// </summary>
        public void ConfigureCurrentProcessWhitelist()
        {
            if (!_isAvailable || _service == null) return;

            try
            {
                string? processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    // 确保白名单模式开启 (IsAppListInverted = false 表示白名单放行)
                    _service.IsAppListInverted = false;

                    if (!_service.ApplicationPaths.Contains(processPath, StringComparer.OrdinalIgnoreCase))
                    {
                        _service.AddApplicationPath(processPath);
                        Debug.WriteLine($"[HidHideManager] 已将进程加入白名单: {processPath}");
                    }

                    if (!_service.IsActive)
                    {
                        _service.IsActive = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HidHideManager] 添加白名单异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 屏蔽指定的小米手柄物理设备实例 ID
        /// </summary>
        public bool CloakDeviceInstance(string instanceId)
        {
            if (!_isAvailable || _service == null || string.IsNullOrEmpty(instanceId))
                return false;

            try
            {
                if (!_service.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
                {
                    _service.AddBlockedInstanceId(instanceId);
                    Debug.WriteLine($"[HidHideManager] 已将设备加入隐藏列表: {instanceId}");
                }

                _service.IsActive = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HidHideManager] 隐藏设备失败 {instanceId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 取消隐藏指定设备实例
        /// </summary>
        public void UncloakDeviceInstance(string instanceId)
        {
            if (!_isAvailable || _service == null || string.IsNullOrEmpty(instanceId))
                return;

            try
            {
                if (_service.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
                {
                    _service.RemoveBlockedInstanceId(instanceId);
                }
            }
            catch { }
        }

        /// <summary>
        /// 清理历史会话遗留的小米手柄屏蔽规则。
        /// 程序异常退出/系统强关时 uncloak 不会执行, 旧实例号 (&N 递增) 的规则会永久残留,
        /// 导致游戏侧看不到手柄。启动时清理是安全的: 当前在线手柄会在被接管时重新隐藏。
        /// </summary>
        public void RemoveStaleXiaomiRules()
        {
            if (!_isAvailable || _service == null) return;

            try
            {
                foreach (string instanceId in _service.BlockedInstanceIds.ToList())
                {
                    if (instanceId.IndexOf("VID&00022717_PID&3144", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _service.RemoveBlockedInstanceId(instanceId);
                        Debug.WriteLine($"[HidHideManager] 已清理遗留屏蔽规则: {instanceId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HidHideManager] 清理遗留规则失败 (需管理员权限): {ex.Message}");
            }
        }
    }
}
