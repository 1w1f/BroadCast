using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using Sdcb;
using System.IO;
using Vortice.Mathematics;
using System.Buffers.Binary;
using Sdcb.FFmpeg.Toolboxs.Extensions;

namespace broadcast.ViewModel
{
    partial class SendViewModel : ObservableObject
    {
        [ObservableProperty]
        bool _isBroadCast = true;
        bool CanStartBroadCast => _isBroadCast;

        [RelayCommand(CanExecute = nameof(CanStartBroadCast))]
        void StartBroadCast()
        {
            StartService();
        }


        void StartService(CancellationToken cancellationToken = default)
        {
            var tcpListener = new TcpListener(new IPEndPoint(IPAddress.Any, 5555));
            cancellationToken.Register(() => tcpListener.Stop());
            tcpListener.Start();
            IsBroadCast = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = tcpListener.AcceptTcpClient();
                Task.Run(() => ServeClient(client, cancellationToken));
            }
        }


        void ServeClient(TcpClient tcpClient, CancellationToken cancellationToken = default)
        {
            try
            {
                using var _ = tcpClient;
                using NetworkStream stream = tcpClient.GetStream();
                using BinaryWriter writer = new(stream);
                RectI screenSize = ScreenCapture.GetScreenSize(screenId: 0);
                RdpCodecParameter rcp = new(AVCodecID.H264, screenSize.Width, screenSize.Height, AVPixelFormat.Bgr0);

                using CodecContext cc = new(Codec.CommonEncoders.Libx264RGB)
                {
                    Width = rcp.Width,
                    Height = rcp.Height,
                    PixelFormat = rcp.PixelFormat,
                    TimeBase = new AVRational(1, 20),
                };
                cc.Open(null, new MediaDictionary
                {
                    ["crf"] = "30",
                    ["tune"] = "zerolatency",
                    ["preset"] = "veryfast"
                });

                writer.Write(rcp.ToArray());
                using Frame source = new();
                foreach (Packet packet in ScreenCapture
                    .CaptureScreenFrames(screenId: 0)
                    .ToBgraFrame()
                    .ConvertFrames(cc)
                    .EncodeFrames(cc))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    writer.Write(packet.Data.Length);
                    writer.Write(packet.Data.AsSpan());
                }
            }
            catch (IOException ex)
            {
                // Unable to write data to the transport connection: 远程主机强迫关闭了一个现有的连接。.
                // Unable to write data to the transport connection: 你的主机中的软件中止了一个已建立的连接。
                //ex.Dump();
            }
        }








    }

    record RdpCodecParameter(AVCodecID CodecId, int Width, int Height, AVPixelFormat PixelFormat)
    {
        public byte[] ToArray()
        {
            byte[] data = new byte[16];
            Span<byte> span = data.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span, (int)CodecId);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], Width);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], Height);
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], (int)PixelFormat);
            return data;
        }
    }
    public static class BgraFrameExtensions
    {
        public static IEnumerable<Frame> ToBgraFrame(this IEnumerable<LockedBgraFrame> bgras)
        {
            using Frame frame = new Frame();
            foreach (LockedBgraFrame bgra in bgras)
            {
                frame.Width = bgra.Width;
                frame.Height = bgra.Height;
                frame.Format = (int)AVPixelFormat.Bgra;
                frame.Data[0] = bgra.DataPointer;
                frame.Linesize[0] = bgra.RowPitch;
                yield return frame;
            }
        }
    }
}
