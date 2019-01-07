using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace CustomVision_ObjectDetection
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MediaCapture mediaCapture;
        private SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private ThreadPoolTimer timer;
        private ObjectDetection objectDetection;

        /// <summary>
        /// 
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            //カメラ初期化
            await InitCameraAsync();

            //モデルファイル読み込み
            StorageFile modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Assets/model.onnx"));

            //ラベル（タグ）設定
            var labels = new List<string>() { "tomato" };

            //認識器初期化
            objectDetection = new ObjectDetection(labels);
            await objectDetection.Init(modelFile);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task InitCameraAsync()
        {
            Debug.WriteLine("InitializeCameraAsync");

            try
            {
                //mediaCaptureオブジェクトが有効な時は一度Disposeする
                if (mediaCapture != null)
                {
                    mediaCapture.Dispose();
                    mediaCapture = null;
                }

                //キャプチャーの設定
                var captureInitSettings = new MediaCaptureInitializationSettings();
                captureInitSettings.VideoDeviceId = "";
                captureInitSettings.StreamingCaptureMode = StreamingCaptureMode.Video;

                //カメラデバイスの取得
                var cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

                if (cameraDevices.Count() == 0)
                {
                    Debug.WriteLine("No Camera");
                    return;
                }
                else if (cameraDevices.Count() == 1)
                {
                    captureInitSettings.VideoDeviceId = cameraDevices[0].Id;
                }
                else
                {
                    captureInitSettings.VideoDeviceId = cameraDevices[1].Id;
                }

                //キャプチャーの準備
                mediaCapture = new MediaCapture();
                await mediaCapture.InitializeAsync(captureInitSettings);

                //キャプチャー設定
                VideoEncodingProperties vp = new VideoEncodingProperties();

                vp.Height = 480;
                vp.Width = 640;

                //カメラによって利用できるSubtypeに違いがあるので、利用できる解像度の内最初に見つかった組み合わせのSubtypeを選択する。
                var resolusions = GetPreviewResolusions(mediaCapture);
                vp.Subtype = resolusions.Find(subtype => subtype.Width == vp.Width).Subtype;

                await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, vp);

                capture.Source = mediaCapture;

                //キャプチャーの開始
                await mediaCapture.StartPreviewAsync();

                Debug.WriteLine("Camera Initialized");

                //15FPS毎にタイマーを起動する。
                TimeSpan timerInterval = TimeSpan.FromMilliseconds(66);
                timer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(CurrentVideoFrame), timerInterval);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="capture"></param>
        /// <returns></returns>
        private List<VideoEncodingProperties> GetPreviewResolusions(MediaCapture capture)
        {
            IReadOnlyList<IMediaEncodingProperties> ret;
            ret = capture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview);

            if (ret.Count <= 0)
            {
                return new List<VideoEncodingProperties>();
            }


            //接続しているカメラの対応解像度やSubtypeを確認するときはコメントを外す
            /*
            foreach (VideoEncodingProperties vp in ret)
            {
                var frameRate = (vp.FrameRate.Numerator / vp.FrameRate.Denominator);
                Debug.WriteLine("{0}: {1}x{2} {3}fps", vp.Subtype, vp.Width, vp.Height, frameRate);
            }
            */

            return ret.Select(item => (VideoEncodingProperties)item).ToList();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="timer"></param>
        private async void CurrentVideoFrame(ThreadPoolTimer timer)
        {
            //複数回の認識はしない
            if (!semaphore.Wait(0)) return;

            try
            {

                VideoFrame previewFrame = new VideoFrame(BitmapPixelFormat.Bgra8, 416, 416);
                await mediaCapture.GetPreviewFrameAsync(previewFrame);

                //認識実行
                var results = await objectDetection.PredictImageAsync(previewFrame);

                //見つかった場合に矩形を描画
                if (results != null)
                {
                    if (results.Count > 0)
                    {
                        var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            drawCanvas(results);
                        });

                    }
                }
                
                //フレームを破棄
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    previewFrame.Dispose();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                semaphore.Release();
            }

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="results"></param>
        private void drawCanvas(IList<PredictionModel> results)
        {

            canvas.Children.Clear();

            foreach (var rst in results)
            {
                if (rst.Probability > 0.75)
                {
                    Rectangle box = new Rectangle();

                    box.Width = (int)(rst.BoundingBox.Width * 640);
                    box.Height = (int)(rst.BoundingBox.Height * 480);
                    box.Fill = new SolidColorBrush(Windows.UI.Colors.Transparent);
                    box.Stroke = new SolidColorBrush(Windows.UI.Colors.Yellow);
                    box.StrokeThickness = 2.0;
                    box.Margin = new Thickness((int)(rst.BoundingBox.Left * 640), (int)(rst.BoundingBox.Top * 480), 0, 0);

                    canvas.Children.Add(box);
                }
            }
        }
    }
}
