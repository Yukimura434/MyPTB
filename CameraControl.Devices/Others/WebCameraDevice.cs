using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Accord.Video;
using Accord.Video.DirectShow;
using CameraControl.Devices.Classes;

namespace CameraControl.Devices.Others
{
    public class WebCameraDevice: BaseCameraDevice
    {
        private VideoCaptureDevice _captureDevice;
        private Dispatcher _owner;

        private LiveViewData _liveViewData = new LiveViewData();

        private volatile bool _operationInProgress;
        private Object _lock = new Object();
        private volatile bool _processLiveView = false;

        
        public WebCameraDevice()
        {
            Capabilities.Add(CapabilityEnum.LiveView);
        }

        public void Init(string id)
        {
            PortName = id;
            _owner = Dispatcher.CurrentDispatcher;
            _captureDevice = new VideoCaptureDevice(id);
            _captureDevice.NewFrame += _localWebCam_NewFrame;
            _captureDevice.SnapshotFrame += _localWebCam_SnapshotFrame;
            _captureDevice.PlayingFinished += CaptureDevice_PlayingFinished;
            _captureDevice.ProvideSnapshots = true;
            IsConnected = true;
            LiveViewImageZoomRatio = new PropertyValue<long>();

            IsoNumber = new PropertyValue<long> { Available = false };
            FNumber = new PropertyValue<long> { Available = false };
            ExposureCompensation = new PropertyValue<long> { Available = false };
            FocusMode = new PropertyValue<long> { Available = false };
            ShutterSpeed = new PropertyValue<long> { Available = false };
            WhiteBalance = new PropertyValue<long> { Available = false };
            Mode = new PropertyValue<long> { Available = false };
            ExposureMeteringMode = new PropertyValue<long> { Available = false };
            CompressionSetting =new PropertyValue<long>();
            VideoCapabilities[] capabilities = _captureDevice.VideoCapabilities;
            for (int i = 0; i < capabilities.Length; i++)
            {
                CompressionSetting.AddValues(capabilities[i].ToString(),i);
            }
            CompressionSetting.ReloadValues();
            if (_captureDevice.VideoResolution != null)
                CompressionSetting.Value = _captureDevice.VideoResolution.ToString();
            else
            {
                if (_captureDevice.VideoCapabilities.Length > 0)
                {
                    _captureDevice.VideoResolution = _captureDevice.VideoCapabilities.First();
                    CompressionSetting.Value = _captureDevice.VideoResolution.ToString();
                }
            }
            CompressionSetting.ValueChanged += CompressionSetting_ValueChanged;
            StartLiveView();

        }

        private void CaptureDevice_PlayingFinished(object sender, ReasonToFinishPlaying reason)
        {
            if (reason == ReasonToFinishPlaying.StoppedByUser) return;
            // DirectShow raises this on its capture worker. Never WaitForStop or
            // run UI/SDK recovery inline there: that would join the current thread.
            _owner.BeginInvoke(new Action(() => HandleSourceFinished(sender)), DispatcherPriority.Normal);
        }

        private void HandleSourceFinished(object source)
        {
            if (!ReferenceEquals(source, _captureDevice) || !IsConnected) return;
            IsConnected = false;
            _processLiveView = false;
            _liveViewData = new LiveViewData();
            OnCameraDisconnected(this, new DisconnectCameraEventArgs());
        }

        private void CompressionSetting_ValueChanged(object sender, string key, long val)
        {
            StopLiveView();
            VideoCapabilities[] capabilities = _captureDevice.VideoCapabilities;
            _captureDevice.VideoResolution = capabilities[val];
            StartLiveView();
        }

        private void _localWebCam_NewFrame(object sender, NewFrameEventArgs eventargs)
        {
            if (_operationInProgress || !_processLiveView)
                return;
            try
            {
                _operationInProgress = true;
                using (Image img = (Bitmap)eventargs.Frame.Clone())
                using (MemoryStream ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Jpeg);
                    _liveViewData.ImageWidth = img.Width;
                    _liveViewData.ImageHeight = img.Height;
                    _liveViewData.LiveViewImageWidth = img.Width;
                    _liveViewData.LiveViewImageHeight = img.Height;
                    _liveViewData.ImageDataPosition = 0;
                    _liveViewData.ImageData = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error get webcam frame ", ex);
            }
        }

        public override void TransferFile(object o, string filename)
        {
            byte[] data = (byte[])o;
            File.WriteAllBytes(filename, data);
        }

        public override void CapturePhotoNoAf()
        {
            CapturePhoto();
        }

        public override  void CapturePhoto()
        {
            if (!IsConnected) throw new InvalidOperationException("Webcam is disconnected.");
            StartLiveView();

            _processLiveView = true;
            GetLiveViewImage();
            if (_liveViewData.ImageData != null)
            {
                PhotoCaptureEvent(_liveViewData.ImageData);
                _liveViewData.ImageData = null;
            }
            else
            {
                throw new Exception("Could not capture photo from webcam.");
            }


            //StopLiveView();
        }

        public override void StartLiveView()
        {
            lock (_lock)
            {
                if (_captureDevice == null || !IsConnected) throw new InvalidOperationException("Webcam is disconnected.");
                if (!_captureDevice.IsRunning)
                {
                    _captureDevice.Start();
                    Thread.Sleep(500);
                }
                _operationInProgress = false;
                _processLiveView = true;
            }
        }

        public override void StopLiveView()
        {
            lock (_lock)
            {
                if (_captureDevice == null) return;
                _captureDevice.SignalToStop();
                _captureDevice.WaitForStop();
                _operationInProgress = false;
                _processLiveView = false;
            }
        }

        public override LiveViewData GetLiveViewImage()
        {
            _operationInProgress = false;
            return IsConnected ? _liveViewData : null;
        }

        private void PhotoCaptureEvent(byte[] data)
        {
            try
            {
                PhotoCapturedEventArgs args = new PhotoCapturedEventArgs
                {
                    WiaImageItem = null,
                    EventArgs = null,
                    CameraDevice = this,
                    FileName = "img0000.jpg",
                    Handle = data
                };
                OnPhotoCapture(this, args);

            }
            catch (Exception e)
            {
                Log.Error("Photo capture error", e);
            }

        }

        private void _localWebCam_SnapshotFrame(object sender, NewFrameEventArgs eventargs)
        {
            try
            {
                System.Drawing.Image img = (Bitmap)eventargs.Frame.Clone();
                using (MemoryStream ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Jpeg);
                    ms.Seek(0, SeekOrigin.Begin);
                    PhotoCaptureEvent(ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Unable to execute event", ex);
            }
        }

        public override void Close()
        {
            IsConnected = false;
            _processLiveView = false;
            _liveViewData = new LiveViewData();
            if (_captureDevice != null)
           {
                _captureDevice.SignalToStop();
                _captureDevice.PlayingFinished -= CaptureDevice_PlayingFinished;
                _captureDevice.SnapshotFrame -= _localWebCam_SnapshotFrame;
                _captureDevice.NewFrame -= _localWebCam_NewFrame;
                _captureDevice.WaitForStop();
                _captureDevice = null;
            }
        }
    }
}
