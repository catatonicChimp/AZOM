using System;
using MozaPlugin.Devices.Motion;

namespace MozaPlugin
{
    /// <summary>
    /// MOZA Motion Controller (HMA150 platform) lane: owned for the life of the plugin
    /// instance. It opens the port itself while the SimHub Motion output or its settings
    /// panel holds it; the 5 s reconnect tick only persists the port and deploys presets.
    /// The SimHub Motion output attaches to it through <see cref="MotionController"/>.
    /// </summary>
    public partial class MozaPlugin
    {
        private MozaMotionController? _motionController;
        private readonly MotionGameFeed _motionFeed = new MotionGameFeed();
        private bool _motionPresetsDeployed;

        internal MozaMotionController? MotionController => _motionController;

        /// <summary>Latest game telemetry as a 0x22/00 frame, fed from DataUpdate.</summary>
        internal MotionGameFeed MotionFeed => _motionFeed;

        /// <summary>Whether SimHub Motion has the HMA150 output in its active setup (status banner).</summary>
        internal Integration.Motion.MotionSetupStatus MotionSetupStatus
            => Integration.Motion.MotionIntegration.GetSetupStatus(_pluginManager);

        private void InitMotionLane()
        {
            _motionController = new MozaMotionController(() => IsShuttingDown);
            if (!string.IsNullOrEmpty(_settings.LastMotionPort))
                _motionController.LastPortName = _settings.LastMotionPort;
            if (!string.IsNullOrEmpty(_settings.LastMotionDeviceId))
                _motionController.LastDeviceId = _settings.LastMotionDeviceId;
        }

        /// <summary>Reconnect-tick hook; same authoritative-device-source gate as the other dedicated lanes.</summary>
        private void TryConnectMotion()
        {
            var lane = _motionController;
            if (lane == null || IsShuttingDown || !lane.IsPresent) return;

            // Presets only once a controller has been seen, so other users get no entry.
            if (!_motionPresetsDeployed)
            {
                Integration.Motion.MotionPresetDeployer.Deploy();
                _motionPresetsDeployed = true;
            }

            if (!lane.IsConnected) return;
            bool dirty = false;
            string port = lane.LastPortName ?? "";
            if (port.Length != 0 && _settings.LastMotionPort != port) { _settings.LastMotionPort = port; dirty = true; }
            string id = lane.LastDeviceId ?? "";
            if (_settings.LastMotionDeviceId != id) { _settings.LastMotionDeviceId = id; dirty = true; }
            if (dirty) ScheduleSave();
        }

        private void DisposeMotionLane()
        {
            try { _motionController?.Dispose(); } catch { }
            _motionController = null;
        }
    }
}
