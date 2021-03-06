﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using Netch.Models;
using Netch.Servers.Shadowsocks;
using Netch.Servers.ShadowsocksR;
using Netch.Servers.Socks5;
using Netch.Servers.Trojan;
using Netch.Servers.VMess;
using Netch.Utils;
using nfapinet;

namespace Netch.Controllers
{
    public class NFController : Guard, IModeController
    {
        private static readonly ServiceController NFService = new ServiceController("netfilter2");

        private static readonly string BinDriver = string.Empty;
        private static readonly string SystemDriver = $"{Environment.SystemDirectory}\\drivers\\netfilter2.sys";
        private static string _sysDns;

        static NFController()
        {
            switch ($"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}")
            {
                case "10.0":
                    BinDriver = "Win-10.sys";
                    break;
                case "6.3":
                case "6.2":
                    BinDriver = "Win-8.sys";
                    break;
                case "6.1":
                case "6.0":
                    BinDriver = "Win-7.sys";
                    break;
                default:
                    Logging.Error($"不支持的系统版本：{Environment.OSVersion.Version}");
                    return;
            }

            BinDriver = "bin\\" + BinDriver;
        }

        public override string Name { get; set; } = "RedirectorMod";

        public override string MainFile { get; protected set; } = "Redirector.exe";

        public NFController()
        {
            InstanceOutputEncoding = "utf-8";
            StartedKeywords.Add("Redirect TCP to");
            // StoppedKeywords.AddRange(new[] {"Failed", "Unable"});
        }

        public bool Start(in Mode mode)
        {
            if (!CheckDriver())
                return false;

            var server = MainController.ServerController.Server;
            //代理进程
            var processes = "";
            //IP过滤
            var processesIPFillter = "";

            //开启进程白名单模式
            if (!Global.Settings.ProcessWhitelistMode)
                processes += "NTT.exe,";

            foreach (var proc in mode.Rule)
            {
                //添加进程代理
                if (proc.EndsWith(".exe"))
                    processes += proc + ",";
                else
                    //添加IP过滤器
                    processesIPFillter += proc + ",";
            }

            var argument = new StringBuilder();

            if (server.Type != "Socks5")
            {
                argument.Append($"-rtcp 127.0.0.1:{Global.Settings.Socks5LocalPort}");
                if (!StartUDPServerAndAppendToArgument(ref argument))
                    return false;
            }
            else
            {
                var result = DNS.Lookup(server.Hostname);
                if (result == null)
                {
                    Logging.Error("无法解析服务器 IP 地址");
                    return false;
                }

                argument.Append($"-rtcp {result}:{server.Port}");

                if (server is Socks5 socks5 && socks5.Auth())
                    argument.Append($" -username \"{socks5.Username}\" -password \"{socks5.Password}\"");

                if (Global.Settings.UDPServer)
                {
                    if (Global.Settings.UDPServerIndex == -1)
                    {
                        argument.Append($" -rudp {result}:{server.Port}");
                    }
                    else
                    {
                        if (!StartUDPServerAndAppendToArgument(ref argument))
                            return false;
                    }
                }
                else
                {
                    argument.Append($" -rudp {result}:{server.Port}");
                }
            }

            //开启进程白名单模式
            argument.Append($" -bypass {Global.Settings.ProcessWhitelistMode.ToString().ToLower()}");
            if (Global.Settings.ProcessWhitelistMode)
                processes += Firewall.ProgramPath.Aggregate(string.Empty, (current, file) => current + Path.GetFileName(file) + ",");

            if (processes.EndsWith(","))
                processes = processes.Substring(0, processes.Length - 1);
            argument.Append($" -p \"{processes}\"");

            // true  除规则内IP全走代理
            // false 仅代理规则内IP
            if (processesIPFillter.EndsWith(","))
            {
                processesIPFillter = processesIPFillter.Substring(0, processesIPFillter.Length - 1);
                argument.Append($" -bypassip {mode.ProcesssIPFillter.ToString().ToLower()}");
                argument.Append($" -fip \"{processesIPFillter}\"");
            }
            else
            {
                argument.Append(" -bypassip true");
            }

            //进程模式代理IP日志打印
            argument.Append($" -printProxyIP {Global.Settings.ProcessProxyIPLog.ToString().ToLower()}");

            //开启进程UDP代理
            argument.Append($" -udpEnable {(!Global.Settings.ProcessNoProxyForUdp).ToString().ToLower()}");

            argument.Append(" -dlog");

            Logging.Info($"Redirector : {argument}");

            for (var i = 0; i < 2; i++)
            {
                State = State.Starting;
                if (!StartInstanceAuto(argument.ToString())) continue;
                if (Global.Settings.ModifySystemDNS)
                {
                    // 备份并替换系统 DNS
                    _sysDns = DNS.OutboundDNS;
                    DNS.OutboundDNS = "1.1.1.1,8.8.8.8";
                }

                return true;
            }

            return false;
        }
        
        private static bool CheckDriver()
        {
            var binFileVersion = Utils.Utils.GetFileVersion(BinDriver);
            var systemFileVersion = Utils.Utils.GetFileVersion(SystemDriver);

            Logging.Info("内置驱动版本: " + binFileVersion);
            Logging.Info("系统驱动版本: " + systemFileVersion);

            if (!File.Exists(BinDriver))
            {
                Logging.Warning("内置驱动不存在");
                if (File.Exists(SystemDriver))
                {
                    Logging.Warning("使用系统驱动");
                    return true;
                }

                Logging.Error("未安装驱动");
                return false;
            }

            if (!File.Exists(SystemDriver))
            {
                return InstallDriver();
            }

            var updateFlag = false;

            if (Version.TryParse(binFileVersion, out var binResult) && Version.TryParse(systemFileVersion, out var systemResult))
            {
                if (binResult.CompareTo(systemResult) > 0)
                {
                    // Bin greater than Installed
                    updateFlag = true;
                }
                else
                {
                    // Installed greater than Bin
                    if (systemResult.Major != binResult.Major)
                    {
                        // API breaking changes
                        updateFlag = true;
                    }
                }
            }
            else
            {
                if (!systemFileVersion.Equals(binFileVersion))
                {
                    updateFlag = true;
                }
            }

            if (!updateFlag) return true;

            Logging.Info("更新驱动");
            UninstallDriver();
            return InstallDriver();
        }

        private bool RestartService()
        {
            try
            {
                switch (NFService.Status)
                {
                    // 启动驱动服务
                    case ServiceControllerStatus.Running:
                        // 防止其他程序占用 重置 NF 百万连接数限制
                        NFService.Stop();
                        NFService.WaitForStatus(ServiceControllerStatus.Stopped);
                        Global.MainForm.StatusText(i18N.Translate("Starting netfilter2 Service"));
                        NFService.Start();
                        break;
                    case ServiceControllerStatus.Stopped:
                        Global.MainForm.StatusText(i18N.Translate("Starting netfilter2 Service"));
                        NFService.Start();
                        break;
                }
            }
            catch (Exception e)
            {
                Logging.Error("启动驱动服务失败：\n" + e);

                var result = NFAPI.nf_registerDriver("netfilter2");
                if (result != NF_STATUS.NF_STATUS_SUCCESS)
                {
                    Logging.Error($"注册驱动失败，返回值：{result}");
                    return false;
                }

                Logging.Info("注册驱动成功");
            }

            return true;
        }

        public static string DriverVersion(string file)
        {
            return File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).FileVersion : string.Empty;
        }

        /// <summary>
        ///     卸载 NF 驱动
        /// </summary>
        /// <returns>是否成功卸载</returns>
        public static bool UninstallDriver()
        {
            Global.MainForm.StatusText(i18N.Translate("Uninstalling NF Service"));
            Logging.Info("卸载 NF 驱动");
            try
            {
                if (NFService.Status == ServiceControllerStatus.Running)
                {
                    NFService.Stop();
                    NFService.WaitForStatus(ServiceControllerStatus.Stopped);
                }
            }
            catch (Exception)
            {
                // ignored
            }

            if (!File.Exists(SystemDriver)) return true;
            NFAPI.nf_unRegisterDriver("netfilter2");
            File.Delete(SystemDriver);

            return true;
        }

        /// <summary>
        ///     安装 NF 驱动
        /// </summary>
        /// <returns>驱动是否安装成功</returns>
        public static bool InstallDriver()
        {
            Logging.Info("安装 NF 驱动");
            try
            {
                File.Copy(BinDriver, SystemDriver);
            }
            catch (Exception e)
            {
                Logging.Error("驱动复制失败\n" + e);
                return false;
            }

            Global.MainForm.StatusText(i18N.Translate("Register driver"));
            // 注册驱动文件
            var result = NFAPI.nf_registerDriver("netfilter2");
            if (result == NF_STATUS.NF_STATUS_SUCCESS)
            {
                Logging.Info($"驱动安装成功");
            }
            else
            {
                Logging.Error($"注册驱动失败，返回值：{result}");
                return false;
            }

            return true;
        }

        public override void Stop()
        {
            var tasks = new Task[]
            {
                Task.Run(() =>
                {
                    if (Global.Settings.ModifySystemDNS)
                        //恢复系统DNS
                        DNS.OutboundDNS = _sysDns;
                }),
                Task.Run(StopInstance),
                Task.Run(() =>
                {
                    if (UdpEncryptedProxy is Guard guard)
                    {
                        try
                        {
                            guard.Stop();
                        }
                        catch (Exception e)
                        {
                            Logging.Error($"停止 {guard.MainFile} 错误：\n" + e);
                        }
                    }
                })
            };
            Task.WaitAll(tasks);
        }

        /// <summary>
        ///     UDP代理进程实例
        /// </summary>
        public IServerController UdpEncryptedProxy;

        private bool StartUDPServerAndAppendToArgument(ref StringBuilder fallback)
        {
            if (Global.Settings.UDPServer)
            {
                if (Global.Settings.UDPServerIndex == -1)
                {
                    fallback.Append($" -rudp 127.0.0.1:{Global.Settings.Socks5LocalPort}");
                }
                else
                {
                    var UDPServer = (Server) Global.Settings.Server.AsReadOnly()[Global.Settings.UDPServerIndex].Clone();

                    var result = DNS.Lookup(UDPServer.Hostname);
                    if (result == null)
                    {
                        Logging.Error("无法解析服务器 IP 地址");
                        return false;
                    }

                    UDPServer.Hostname = result.ToString();

                    if (UDPServer.Type != "Socks5")
                    {
                        //启动UDP分流服务支持SS/SSR/Trojan
                        UdpEncryptedProxy = UDPServer.Type switch
                        {
                            "SS" => new SSController(),
                            "SSR" => new SSRController(),
                            "VMess" => new VMessController(),
                            "Trojan" => new TrojanController(),
                            _ => UdpEncryptedProxy
                        };
                        UdpEncryptedProxy.Socks5LocalPort = (ushort?) (Global.Settings.Socks5LocalPort + 1);
                        UdpEncryptedProxy.Name += "Udp";
                        var mode = new Mode
                        {
                            Remark = "UdpServer",
                            Type = 4
                        };
                        try
                        {
                            if (!UdpEncryptedProxy.Start((Server) UDPServer.Clone(), mode))
                            {
                                return false;
                            }
                        }
                        catch (Exception e)
                        {
                            Logging.Error("Udp加密代理启动失败: " + e.Message);
                            return false;
                        }

                        fallback.Append($" -rudp 127.0.0.1:{Global.Settings.Socks5LocalPort + 1}");
                    }
                    else
                    {
                        fallback.Append($" -rudp {UDPServer.Hostname}:{UDPServer.Port}");
                    }
                }
            }
            else
            {
                fallback.Append($" -rudp 127.0.0.1:{Global.Settings.Socks5LocalPort}");
            }

            return true;
        }
    }
}