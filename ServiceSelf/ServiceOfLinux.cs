﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ServiceSelf
{
    [SupportedOSPlatform("linux")]
    sealed class ServiceOfLinux : Service
    {
        [DllImport("libc", SetLastError = true)]
        private static extern uint geteuid();

        public ServiceOfLinux(string name)
            : base(name)
        {
        }

        public override void CreateStart(string filePath, IEnumerable<Argument>? arguments, string? workingDirectory, string? description)
        {
            CheckRoot();

            filePath = Path.GetFullPath(filePath);

            var unitFilePath = $"/etc/systemd/system/{this.Name}.service";
            var oldFilePath = QueryServiceFilePath(unitFilePath);

            if (string.IsNullOrEmpty(oldFilePath) == false &&
                filePath.Equals(oldFilePath, StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new InvalidOperationException("系统已存在同名但不同路径的服务");
            }

            var unitContent = CreateUnitContent(filePath, arguments, workingDirectory, description);
            File.WriteAllText(unitFilePath, unitContent);

            // SELinux
            Shell("chcon", $"--type=bin_t {filePath}", false);

            SystemCtl("daemon-reload");
            SystemCtl($"start {this.Name}.service");
            SystemCtl($"enable {this.Name}.service", false);
        }

        private static string? QueryServiceFilePath(string unitFilePath)
        {
            if (File.Exists(unitFilePath) == false)
            {
                return null;
            }

            var execStartPrefix = "ExecStart=".AsSpan();
            var wantedByPrefix = "WantedBy=".AsSpan();

            using var stream = File.OpenRead(unitFilePath);
            var reader = new StreamReader(stream);

            var filePath = ReadOnlySpan<char>.Empty;
            var wantedBy = ReadOnlySpan<char>.Empty;
            while (reader.EndOfStream == false)
            {
                var line = reader.ReadLine().AsSpan();
                if (line.StartsWith(execStartPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    line = line[execStartPrefix.Length..];
                    var index = line.IndexOf(' ');
                    filePath = index < 0 ? line : line[..index];
                }
                else if (line.StartsWith(wantedByPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    wantedBy = line[wantedByPrefix.Length..].Trim();
                }

                if (filePath.Length > 0 && wantedBy.Length > 0)
                {
                    break;
                }
            }

            if (filePath.IsEmpty || wantedBy.IsEmpty)
            {
                return null;
            }

            var wants = $"{wantedBy.ToString()}.wants";
            var unitFileName = Path.GetFileName(unitFilePath);
            var unitFileDir = Path.GetDirectoryName(unitFilePath);
            var unitLink = Path.Combine(unitFileDir!, wants, unitFileName);
            return File.Exists(unitLink) ? filePath.ToString() : null;
        }


        private static string CreateUnitContent(string filePath, IEnumerable<Argument>? arguments, string? workingDirectory, string? description)
        {
            workingDirectory = string.IsNullOrEmpty(workingDirectory)
                ? Path.GetDirectoryName(filePath)
                : Path.GetFullPath(workingDirectory);

            var execStart = arguments == null
                ? filePath
                : $"{filePath} {string.Join(' ', arguments)}";

            return new StringBuilder()
                .AppendLine("[Unit]")
                .AppendLine($"Description={description}")
                .AppendLine()
                .AppendLine("[Service]")
                .AppendLine("Type=notify")
                .AppendLine($"ExecStart={execStart}")
                .AppendLine($"WorkingDirectory={workingDirectory}")
                .AppendLine()
                .AppendLine("[Install]")
                .AppendLine("WantedBy=multi-user.target")
                .ToString();
        }


        public override void StopDelete()
        {
            CheckRoot();

            var unitFilePath = $"/etc/systemd/system/{this.Name}.service";
            if (File.Exists(unitFilePath) == false)
            {
                return;
            }

            SystemCtl($"stop {this.Name}.service");
            SystemCtl($"disable {this.Name}.service", false);
            SystemCtl("daemon-reload");

            File.Delete(unitFilePath);
        }

        private static void SystemCtl(string arguments, bool showError = true)
        {
            Shell("systemctl", arguments, showError);
        }

        private static void Shell(string fileName, string arguments, bool showError = true)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = !showError
            };
            Process.Start(startInfo)?.WaitForExit();
        }

        private static void CheckRoot()
        {
            if (geteuid() != 0)
            {
                throw new UnauthorizedAccessException("无法操作服务：没有root权限");
            }
        }
    }
}
