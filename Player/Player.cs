﻿/*
    This file is part of Snap.Net
    Copyright (C) 2020  Stijn Van der Borght
    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
using CliWrap;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapDotNet.Player
{
    public enum EState
    {
        Playing,
        Stopped
    }

    /// <summary>
    /// This class is responsible for managing snapclient processes
    /// </summary>
    public class Player
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private Dictionary<string, CancellationTokenSource> m_ActivePlayers = new Dictionary<string, CancellationTokenSource>();

        public event Action<string, EState> DevicePlayStateChanged = null;

        public event Action OnSnapClientErrored;

        private static Dictionary<string, Tuple<string, string>> s_WASAPIErrorCodes = new Dictionary<string, Tuple<string, string>>()
        {
            {"88890004", new Tuple<string, string>("AUDCLNT_E_DEVICE_INVALIDATED", "The audio endpoint device has been unplugged, or the audio hardware or associated hardware resources have been reconfigured, disabled, removed, or otherwise made unavailable for use.") },
            {"88890008", new Tuple<string, string>("AUDCLNT_E_UNSUPPORTED_FORMAT", "The audio engine (shared mode) or audio endpoint device (exclusive mode) does not support the specified format.") },
            {"88890017", new Tuple<string, string>("AUDCLNT_E_CPUUSAGE_EXCEEDED", "The audio endpoint device has been unplugged, or the audio hardware or associated hardware rIndicates that the process-pass duration exceeded the maximum CPU usage. The audio engine keeps track of CPU usage by maintaining the number of times the process-pass duration exceeds the maximum CPU usage. The maximum CPU usage is calculated as a percent of the engine's periodicity. The percentage value is the system's CPU throttle value (within the range of 10% and 90%). If this value is not found, then the default value of 40% is used to calculate the maximum CPU usage.esources have been reconfigured, disabled, removed, or otherwise made unavailable for use.") },
            {"88890003", new Tuple<string, string>("AUDCLNT_E_WRONG_ENDPOINT_TYPE", "The AUDCLNT_STREAMFLAGS_LOOPBACK flag is set but the endpoint device is a capture device, not a rendering device.") },
            {"88890001", new Tuple<string, string>("AUDCLNT_E_NOT_INITIALIZED", "The IAudioClient object is not initialized.") },
            {"88890005", new Tuple<string, string>("AUDCLNT_E_NOT_STOPPED", "The audio stream was not stopped at the time of the Start call.") },
            {"88890011", new Tuple<string, string>("AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED", "The audio stream was not initialized for event-driven buffering.") },
            {"8889000F", new Tuple<string, string>("AUDCLNT_E_ENDPOINT_CREATE_FAILED", "The method failed to create the audio endpoint for the render or the capture device. This can occur if the audio endpoint device has been unplugged, or the audio hardware or associated hardware resources have been reconfigured, disabled, removed, or otherwise made unavailable for use.") },
            {"88890006", new Tuple<string, string>("AUDCLNT_E_BUFFER_TOO_LARGE", "The NumFramesRequested value exceeds the available buffer space (buffer size minus padding size).") },
            {"88890018", new Tuple<string, string>("AUDCLNT_E_BUFFER_ERROR", "GetBuffer failed to retrieve a data buffer and *ppData points to NULL. For more information, see Remarks.") },
            {"88890016", new Tuple<string, string>("AUDCLNT_E_BUFFER_SIZE_ERROR", "Indicates that the buffer duration value requested by an exclusive-mode client is out of range. The requested duration value for pull mode must not be greater than 500 milliseconds; for push mode the duration value must not be greater than 2 seconds.") },
            {"88890019", new Tuple<string, string>("AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED", "The requested buffer size is not aligned. This code can be returned for a render or a capture device if the caller specified AUDCLNT_SHAREMODE_EXCLUSIVE and the AUDCLNT_STREAMFLAGS_EVENTCALLBACK flags. The caller must call Initialize again with the aligned buffer size. For more information, see Remarks.") },
            {"8889000C", new Tuple<string, string>("AUDCLNT_E_THREAD_NOT_REGISTERED", "The thread is not registered.") },
            {"8889000A", new Tuple<string, string>("AUDCLNT_E_DEVICE_IN_USE", "The endpoint device is already in use. Either the device is being used in exclusive mode, or the device is being used in shared mode and the caller asked to use the device in exclusive mode.") },
            {"88890014", new Tuple<string, string>("AUDCLNT_E_EVENTHANDLE_NOT_SET", "The audio stream is configured to use event-driven buffering, but the caller has not called IAudioClient::SetEventHandle to set the event handle on the stream.") },
            {"88890015", new Tuple<string, string>("AUDCLNT_E_INCORRECT_BUFFER_SIZE", "Indicates that the buffer has an incorrect size.") },
            {"88890012", new Tuple<string, string>("AUDCLNT_E_EXCLUSIVE_MODE_ONLY", "Exclusive mode only.") },
            {"88890010", new Tuple<string, string>("AUDCLNT_E_SERVICE_NOT_RUNNING", "The Windows audio service is not running.") },
            {"88890013", new Tuple<string, string>("AUDCLNT_E_BUFDURATION_PERIOD_NOT_EQUAL", "The AUDCLNT_STREAMFLAGS_EVENTCALLBACK flag is set but parameters hnsBufferDuration and hnsPeriodicity are not equal.") },
            {"88890009", new Tuple<string, string>("AUDCLNT_E_INVALID_SIZE", "The NumFramesWritten value exceeds the NumFramesRequested value specified in the previous IAudioRenderClient::GetBuffer call") },
        };

        public Player()
        {

        }

        /// <summary>
        /// Checks which devices need to start playing on startup, and plays them
        /// This method should only be called after the control port has connected successfully
        /// </summary>
        public async Task StartAutoPlayAsync()
        {
            // if we were already playing, stop
            if (m_ActivePlayers.Count > 0)
            {
                StopAll();
            }

            // load settings, see if we need to start auto-playing
            Dictionary<string, Tuple<bool, string>> deviceAutoPlayFlags = SnapSettings.GetDeviceAutoPlayFlags();
            List<Task> tasks = new List<Task>();
            foreach (KeyValuePair<string, Tuple<bool, string>> kvp in deviceAutoPlayFlags)
            {
                if (kvp.Value.Item1 == true)
                {
                    Logger.Info("Starting auto-play for {0}", kvp.Value.Item2);
                    tasks.Add(PlayAsync(kvp.Key, kvp.Value.Item2));
                }
            }
            await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
        }

        public void StopAll()
        {
            string[] ids = m_ActivePlayers.Keys.ToArray();
            foreach (string id in ids)
            {
                this.Stop(id);
            }
        }

        public EState GetState(string deviceUniqueId)
        {
            return m_ActivePlayers.ContainsKey(deviceUniqueId) ? EState.Playing : EState.Stopped;
        }

        public async Task PlayAsync(string deviceUniqueId, string friendlyName)
        {
            DevicePlayStateChanged?.Invoke(deviceUniqueId, EState.Playing);
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            if (m_ActivePlayers.ContainsKey(deviceUniqueId) == false)
            {
                m_ActivePlayers.Add(deviceUniqueId, cancellationTokenSource);
            }
            else
            {
                m_ActivePlayers[deviceUniqueId] = cancellationTokenSource;
                Logger.Error("device {0} already existed in m_ActivePlayers", friendlyName);
            }
            try
            {
                await _PlayAsync(deviceUniqueId, cancellationTokenSource).ConfigureAwait(false);
            }
            catch (NullReferenceException e)
            {
                Logger.Error(e, "Error while trying to play, device {0}", friendlyName);
                Snapcast.Instance.ShowNotification("Device not found", string.Format("Couldn't find sound device '{0}'. Has it been unplugged?", friendlyName));
                DevicePlayStateChanged?.Invoke(deviceUniqueId, EState.Stopped);
                m_ActivePlayers.Remove(deviceUniqueId);
                if (System.Windows.MessageBox.Show(string.Format("The audio device '{0}' had been marked for auto-play, but is missing from the system. Would you like to remove it from auto-play?", friendlyName),
                    "Snap.Net - Auto-play device missing", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
                {
                    SnapSettings.SetAudioDeviceAutoPlay(deviceUniqueId, false, friendlyName);
                }
            }
        }

        private string _SnapClient()
        {
            return Path.Combine(Utils.GetApplicationDirectory(), "SnapClient", "snapclient.exe");
        }

        private Device _GetDevice(string uniqueId)
        {
            Device[] devices = Device.GetDevices();
            foreach (Device d in devices)
            {
                if (d.UniqueId == uniqueId)
                {
                    return d;
                }
            }
            return null;
        }

        private async Task _PlayAsync(string deviceUniqueId, CancellationTokenSource cancellationTokenSource)
        {
            Device device = _GetDevice(deviceUniqueId);
            if (device == null)
            {
                await Task.FromException(new NullReferenceException("Couldn't get device with uniqueId " + deviceUniqueId)).ConfigureAwait(false);
            }
            else
            {
                int deviceInstanceId = SnapSettings.DetermineInstanceId(deviceUniqueId, device.Index);

                if (deviceInstanceId != -1)
                {
                    StringBuilder stdError = new StringBuilder();
                    string lastLine = "";
                    Action<string> stdOut = (line) =>
                    {
                        lastLine = line; // we only are about the last line from the output - in case there's an error (snapclient should probably ben sending these to stderr though)
                    };
                    string command = string.Format("-h {0} -p {1} -s {2} -i {3}", SnapSettings.Server, SnapSettings.PlayerPort, device.Index, deviceInstanceId);
                    Logger.Debug("Snapclient command: {0}", command);
                    CommandTask<CommandResult> task = Cli.Wrap(_SnapClient())
                                                        .WithArguments(command)
                                                        .WithStandardOutputPipe(PipeTarget.ToDelegate(stdOut))
                                                        .ExecuteAsync(cancellationTokenSource.Token);

                    Logger.Debug("Snapclient PID: {0}", task.ProcessId);
                    ChildProcessTracker.AddProcess(Process.GetProcessById(task.ProcessId)); // this utility helps us make sure the player process doesn't keep going if our process is killed / crashes
                    try
                    {
                        await task;
                    }
                    catch (CliWrap.Exceptions.CommandExecutionException e)
                    {
                        OnSnapClientErrored?.Invoke();
                        // add type to ShowNotification (level?), show notification with type and print log at type level
                        Snapcast.Instance.ShowNotification("Snapclient error", _BuildErrorMessage(device, lastLine));
                        // todo: parse WASAPI error code here and provide human friendly output
                        Logger.Error("Snapclient exited with non-zero exit code. Exception:");
                        Logger.Error(e.Message);
                        DevicePlayStateChanged?.Invoke(deviceUniqueId, EState.Stopped);
                        m_ActivePlayers.Remove(deviceUniqueId);
                    }
                }
            }
            DevicePlayStateChanged?.Invoke(deviceUniqueId, EState.Stopped);
            m_ActivePlayers.Remove(deviceUniqueId);
        }

        private string _BuildErrorMessage(Device device, string lastLine)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("Device: {0}\n\n", device.Name));
            sb.Append(string.Format("Last line from stdOut was: {0}\n\n", lastLine));
            if (lastLine.Contains("WASAPI"))
            {
                string errorCode = _FindErrorCode(lastLine);
                if (string.IsNullOrEmpty(errorCode) == false)
                {
                    sb.Append(string.Format("...that's WASAPI-nese for {0} - {1})", s_WASAPIErrorCodes[errorCode].Item1, s_WASAPIErrorCodes[errorCode].Item2));
                }
            }
            return sb.ToString();
        }

        private string _FindErrorCode(string line)
        {
            string[] split = line.Split(' ');
            foreach (string s in split)
            {
                if (s_WASAPIErrorCodes.ContainsKey(s))
                {
                    return s;
                }
            }
            return "";
        }

        public void Stop(string deviceUniqueId)
        {
            if (m_ActivePlayers.ContainsKey(deviceUniqueId))
            {
                m_ActivePlayers[deviceUniqueId].Cancel();
                DevicePlayStateChanged?.Invoke(deviceUniqueId, EState.Stopped);
                m_ActivePlayers.Remove(deviceUniqueId);
            }
        }
    }
}
