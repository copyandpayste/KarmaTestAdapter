﻿using KarmaTestAdapter.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TwoPS.Processes;

namespace KarmaTestAdapter.Karma
{
    public enum KarmaServerState
    {
        None,
        Starting,
        Running
    }

    public class KarmaServer
    {
        public KarmaServer(string karmaConfigFile, IKarmaLogger logger)
        {
            Logger = logger;

            if (string.IsNullOrWhiteSpace(karmaConfigFile))
            {
                throw new ArgumentNullException("karmaConfigFile");
            }

            if (!File.Exists(karmaConfigFile))
            {
                throw new ArgumentException("Configuration file does not exist: " + karmaConfigFile, "karmaConfigFile");
            }

            KarmaConfigFile = karmaConfigFile;
            State = KarmaServerState.None;
        }

        public IKarmaLogger Logger { get; private set; }
        public string KarmaConfigFile { get; private set; }
        public string WorkingDirectory { get { return Path.GetDirectoryName(KarmaConfigFile); } }
        public string StartScript { get { return Path.Combine(Globals.LibDirectory, "Start.js"); } }
        public string NodePath { get { return string.Join(";", GetNodePath(WorkingDirectory)); } }
        public KarmaServerState State { get; private set; }
        public int Port { get; private set; }

        private TaskCompletionSource<int> _finishedSource;
        public Task<int> Finished { get { return _finishedSource.Task; } }

        private IEnumerable<string> GetNodePath(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                if (Directory.Exists(Path.Combine(directory, "node_modules")))
                {
                    yield return Path.Combine(directory, "node_modules");
                }
                foreach (var path in GetNodePath(Path.GetDirectoryName(directory)))
                {
                    yield return path;
                }
            }
        }

        public ProcessOptions GetProcessOptions()
        {
            if (!Directory.Exists(WorkingDirectory))
            {
                throw new Exception("Could not find the working directory");
            }

            if (!File.Exists(StartScript))
            {
                throw new Exception("Could not find start script for KarmaTestAdapter (" + StartScript + ")");
            }

            var options = new ProcessOptions("node")
            {
                WorkingDirectory = WorkingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            options.EnvironmentVariables["NODE_PATH"] = NodePath;

            options.Add(StartScript);
            options.Add("--karma", Path.GetFileName(KarmaConfigFile));

            return options;
        }

        public event Action<int> Started;
        private void OnStarted(int port)
        {
            if (Started != null)
            {
                Started(port);
            }
        }

        public event Action<int?, Exception> Stopped;
        private void OnStopped(int? exitCode, Exception ex)
        {
            if (Stopped != null)
            {
                Stopped(exitCode, ex);
            }
        }

        public event Action<string> OutputReceived;
        private void OnOutputReceived(string line)
        {
            if (OutputReceived != null)
            {
                OutputReceived(line);
            }
        }

        public event Action<string> ErrorReceived;
        private void OnErrorReceived(string line)
        {
            if (ErrorReceived != null)
            {
                ErrorReceived(line);
            }
        }

        private Process _process;
        public Task<int> StartServer(int timeout = Timeout.Infinite)
        {
            switch (State)
            {
                case KarmaServerState.Starting:
                    throw new Exception("The karma server is already starting");
                case KarmaServerState.Running:
                    throw new Exception("The karma server is already running");
            }

            Port = 0;
            State = KarmaServerState.Starting;

            _finishedSource = new TaskCompletionSource<int>();

            var portSource = new TaskCompletionSource<int>().SetTimeout(timeout);

            _process = new Process(GetProcessOptions());
            _process.StandardOutputRead += (s, e) =>
            {
                if (Port == 0)
                {
                    var match = Regex.Match(e.Line, @"\[VS Server\]: Started - port: (\d+)");
                    if (match.Success)
                    {
                        Port = int.Parse(match.Groups[1].Value);
                        State = KarmaServerState.Running;
                        OnStarted(Port);
                        portSource.TrySetResult(Port);
                    }
                }
            };
            _process.StandardOutputRead += (s, e) => this.OnOutputReceived(e.Line);
            _process.StandardErrorRead += (s, e) => this.OnErrorReceived(e.Line);

            Logger.Debug("Starting Karma: {0}", _process.Options.CommandLine);
            var runTask = Task.Run(() =>
            {
                try
                {
                    return _process.Run();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "KarmaServer error");
                    throw;
                }
            });

            runTask.ContinueWith(t =>
            {
                State = KarmaServerState.None;
                portSource.TrySetCanceled();
                _finishedSource.TrySetResult(t.Result.ExitCode);
                OnStopped(t.Result.ExitCode, t.Exception);
            });

            return portSource.Task;
        }

        public void Kill(string reason)
        {
            if (_process != null)
            {
                Logger.Warn("Killing karma server: {0}", reason);
                _process.Cancel();
            }
        }
    }
}