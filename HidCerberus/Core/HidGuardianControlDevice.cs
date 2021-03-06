﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidCerberus.Util;
using JsonConfig;
using PInvoke;
using Polly;
using Serilog;

namespace HidCerberus.Core
{
    public delegate void OpenPermissionRequestedEventHandler(object sender, OpenPermissionRequestedEventArgs args);

    public class OpenPermissionRequestedEventArgs : EventArgs
    {
        public OpenPermissionRequestedEventArgs(IEnumerable<string> ids, int pid)
        {
            HardwareIds = ids;
            ProcessId = pid;
        }

        public IEnumerable<string> HardwareIds { get; }

        public int ProcessId { get; }

        public bool IsAllowed { get; set; }

        public bool IsPermanent { get; set; }
    }

    public sealed partial class HidGuardianControlDevice : IDisposable
    {
        private readonly Kernel32.SafeObjectHandle _deviceHandle;
        private readonly List<Task> _invertedCallTasks = new List<Task>();
        private readonly CancellationTokenSource _invertedCallTokenSource = new CancellationTokenSource();
        private readonly Random _randGen = new Random();

        public HidGuardianControlDevice()
        {
            _deviceHandle = Kernel32.CreateFile(DevicePath,
                Kernel32.ACCESS_MASK.GenericRight.GENERIC_READ | Kernel32.ACCESS_MASK.GenericRight.GENERIC_WRITE,
                Kernel32.FileShare.FILE_SHARE_READ | Kernel32.FileShare.FILE_SHARE_WRITE,
                IntPtr.Zero, Kernel32.CreationDisposition.OPEN_EXISTING,
                Kernel32.CreateFileFlags.FILE_ATTRIBUTE_NORMAL
                | Kernel32.CreateFileFlags.FILE_FLAG_NO_BUFFERING
                | Kernel32.CreateFileFlags.FILE_FLAG_WRITE_THROUGH
                | Kernel32.CreateFileFlags.FILE_FLAG_OVERLAPPED,
                Kernel32.SafeObjectHandle.Null
            );

            if (_deviceHandle.IsInvalid)
                throw new ArgumentException($"Couldn't open device {DevicePath}");

            var maxThreads = (int)Config.Default.Core.HGCD.Performance.WorkerThreads;
            var completionPortThreads = (int)Config.Default.Core.HGCD.Performance.CompletionPortThreads;

            ThreadPool.SetMinThreads(maxThreads, completionPortThreads);

            Log.Information("Spawning {MaxThreads} threads", maxThreads);

            for (var i = 0; i < maxThreads; i++)
                _invertedCallTasks.Add(
                    Task.Factory.StartNew(InvertedCallSupplierWorker, _invertedCallTokenSource.Token));
        }

        /// <summary>
        ///     Gets the path to the control device of HidGuardian.
        /// </summary>
        private static string DevicePath => "\\\\.\\HidGuardian";

        public event OpenPermissionRequestedEventHandler OpenPermissionRequested;

        private void InvertedCallSupplierWorker(object cancellationToken)
        {
            Policy.Handle<Win32Exception>().Retry(3,
                (exception, i, ctx) =>
                {
                    Log.Error("Inverted Call Thread died with exception {@Exception}, trying to restart...", exception);
                }).Execute(() =>
            {
                var token = (CancellationToken)cancellationToken;

                var invertedCallSize = Marshal.SizeOf<HidGuardianGetCreateRequest>();
                var invertedCallBuffer = Marshal.AllocHGlobal(invertedCallSize);

                var authCallSize = Marshal.SizeOf<HidGuardianSetCreateRequest>();
                var authCallBuffer = Marshal.AllocHGlobal(authCallSize);

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Create random value to match request/response pair
                        var requestId = (uint)_randGen.Next();

                        // Craft inverted call packet
                        Marshal.StructureToPtr(
                            new HidGuardianGetCreateRequest
                            {
                                Size = (uint)invertedCallSize,
                                RequestId = requestId
                            },
                            invertedCallBuffer, false);

                        // Send inverted call (this will block until the driver receives an open request)
                        var ret = _deviceHandle.OverlappedDeviceIoControl(
                            IoctlHidguardianGetCreateRequest,
                            invertedCallBuffer,
                            invertedCallSize,
                            invertedCallBuffer,
                            invertedCallSize,
                            out var _);

                        if (!ret)
                            throw new Win32Exception(Marshal.GetLastWin32Error());

                        // Get back modified values from driver
                        var request = Marshal.PtrToStructure<HidGuardianGetCreateRequest>(invertedCallBuffer);

                        // Invoke open permission request so we know what to do next
                        var eventArgs =
                            new OpenPermissionRequestedEventArgs(ExtractHardwareIds(request), (int)request.ProcessId);
                        OpenPermissionRequested?.Invoke(this, eventArgs);

                        // Craft authentication request packet
                        Marshal.StructureToPtr(
                            new HidGuardianSetCreateRequest
                            {
                                RequestId = request.RequestId,
                                DeviceIndex = request.DeviceIndex,
                                IsAllowed = eventArgs.IsAllowed,
                                IsSticky = eventArgs.IsPermanent
                            },
                            authCallBuffer, false);

                        // This request will dequeue the pending request and either complete it successfully or fail it
                        ret = _deviceHandle.OverlappedDeviceIoControl(
                            IoctlHidguardianSetCreateRequest,
                            authCallBuffer,
                            authCallSize,
                            authCallBuffer,
                            authCallSize,
                            out var _);

                        if (!ret)
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(invertedCallBuffer);
                }
            });
        }

        /// <summary>
        ///     Splits a UTF-16 multi-string into a standard UTF-8 string array.
        /// </summary>
        /// <param name="request">The <see cref="HidGuardianControlDevice.HidGuardianGetCreateRequest" /> to parse.</param>
        /// <returns>The array containing the extracted hardware IDs.</returns>
        private static IEnumerable<string> ExtractHardwareIds(HidGuardianGetCreateRequest request)
        {
            var multibyte = Encoding.Unicode.GetString(request.HardwareIds).TrimEnd('\0');

            return Encoding.UTF8.GetBytes(multibyte).Separate(new byte[] { 0x00 })
                .Select(chunk => Encoding.UTF8.GetString(chunk)).ToList();
        }

        #region IDisposable Support

        private bool disposedValue; // To detect redundant calls

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Log.Information("Notifying threads to quit");
                    _invertedCallTokenSource.Cancel();
                }

                Log.Information("Closing handle to control device...");
                _deviceHandle?.Close();
                Log.Information("Handle closed");

                disposedValue = true;
            }
        }

        ~HidGuardianControlDevice()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}