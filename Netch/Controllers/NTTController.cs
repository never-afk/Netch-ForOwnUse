﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using Netch.Models;
using Netch.Utils;

namespace Netch.Controllers
{
    public class NTTController : Controller
    {
        private string _lastResult;

        public NTTController()
        {
            Name = "NTT";
            MainFile = "NTT.exe";
        }

        /// <summary>
        ///     启动 NatTypeTester
        /// </summary>
        /// <returns></returns>
        public (bool, string, string, string) Start()
        {
            try
            {
                Instance = GetProcess();

                Instance.StartInfo.Arguments = $" {Global.Settings.STUN_Server} {Global.Settings.STUN_Server_Port}";

                Instance.OutputDataReceived += OnOutputDataReceived;
                Instance.ErrorDataReceived += OnOutputDataReceived;

                State = State.Starting;
                Instance.Start();
                Instance.BeginOutputReadLine();
                Instance.BeginErrorReadLine();
                Instance.WaitForExit();

                var result = _lastResult.Split('#');
                var natType = result[0];
                var localEnd = result[1];
                var publicEnd = result[2];

                return (true, natType, localEnd, publicEnd);
            }
            catch (Win32Exception e)
            {
                Logging.Error("NTT 进程出错\n" + e);
                Stop();
                return (false, null, null, null);
            }
        }

        private new void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
                _lastResult = e.Data;
        }

        public override void Stop()
        {
            StopInstance();
        }
    }
}