﻿using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog.Common;
using NLog.Config;

namespace NLog.Targets.Http
{
    [Target("HTTP")]
    public class HTTP : TargetWithLayout
    {
        private readonly ConcurrentQueue<string> _taskQueue = new ConcurrentQueue<string>();
        private readonly CancellationTokenSource _terminateProcessor = new CancellationTokenSource();
        private readonly SemaphoreSlim conversationActiveFlag = new SemaphoreSlim(1, 1);

        public HTTP()
        {
            if (BatchSize == 0) ++BatchSize;
            var task = Task.Factory.StartNew(() =>
                {
                    while (!_terminateProcessor.IsCancellationRequested)
                    {
                        var counter = 0;
                        var sb = new StringBuilder();
                        while (!_taskQueue.IsEmpty)
                        {
                            _taskQueue.TryDequeue(out var message);
                            if (message != null)
                            {
                                ++counter;
                                sb.AppendLine(message);
                                if (!_taskQueue.IsEmpty)
                                    sb.AppendLine();
                            }

                            if (counter==BatchSize)
                            {
                                SendFast(sb.ToString());
                                sb.Clear();
                                counter = 0;
                            }
                        }

                        if (sb.Length > 0) SendFast(sb.ToString());
                        Thread.Sleep(1);
                    }
                }, _terminateProcessor.Token, TaskCreationOptions.None,
                TaskScheduler.Default);
            while(task.Status!=TaskStatus.Running)Thread.Sleep(1);
        }

        [RequiredParameter] public string URL { get; set; }

        public string Authorization { get; set; }

        public bool IgnoreSslErrors { get; set; } = true;

        public int BatchSize { get; set; }

        protected override void CloseTarget()
        {
            _terminateProcessor.Cancel(false);
            base.CloseTarget();
        }

        protected override void FlushAsync(AsyncContinuation asyncContinuation)
        {
            // If there are messages to be processed
            // or no flags available 
            // just wait
            while (!_taskQueue.IsEmpty || conversationActiveFlag.CurrentCount == 0) Thread.Sleep(1);
            base.FlushAsync(asyncContinuation);
        }

        protected override void Write(LogEventInfo logEvent)
        {
            _taskQueue.Enqueue(Layout.Render(logEvent));
        }

        private void SendFast(string message)
        {
            conversationActiveFlag.Wait(_terminateProcessor.Token);
            try
            {
                var http = (HttpWebRequest) WebRequest.Create(URL);
                http.KeepAlive = false;
                http.Method = "POST";
                if (IgnoreSslErrors)
                    http.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
                if (!string.IsNullOrWhiteSpace(Authorization)) http.Headers.Add("Authorization", Authorization);

                var bytes = Encoding.ASCII.GetBytes(message);
                http.ContentLength = bytes.Length;
                using (var os = http.GetRequestStream())
                {
                    os.Write(bytes, 0, bytes.Length); //Push it out there
                    os.Close();
                }

                using (var response = http.GetResponseAsync())
                {
                    using (var stream = response.Result.GetResponseStream())
                    {
                        using (var sr = new StreamReader(stream))
                        {
                            var content = sr.ReadToEnd();
                        }
                    }
                }
            }
            finally
            {
                conversationActiveFlag.Release();
            }
        }
    }
}