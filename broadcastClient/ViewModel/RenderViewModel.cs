using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using Sdcb.FFmpeg.Common;
using Vanara.Extensions;
using System.Windows.Media.Imaging;
using static Vanara.PInvoke.Gdi32;
using System.Windows.Media;

namespace broadcastClient.ViewModel
{
    partial class RenderViewModel : ObservableObject
    {

        private WriteableBitmap _screenBitmap;
        public WriteableBitmap ScreenBitmap
        {
            get { return _screenBitmap; }
            set { _screenBitmap = value; OnPropertyChanged(nameof(ScreenBitmap)); }
        }


        unsafe private byte* _imageBufferPointer;
        unsafe public byte* ImageBufferPointer
        {
            get { return _imageBufferPointer; }
            set { _imageBufferPointer = value; OnPropertyChanged(nameof(ImageBufferPointer)); }
        }

        [ObservableProperty]
        bool isRender = true;


        public RenderViewModel()
        {
            _screenBitmap = new WriteableBitmap(1920, 1080, 96, 96, PixelFormats.Bgra32, null);
            OnPropertyChanged(nameof(ScreenBitmap));

        }

        [RelayCommand]
        void StartRender()
        {
            Task decodingTask = Task.Run(() => DecodeThread(() => (1920, 1080)));
            //unsafe
            //{
            //    ImageBufferPointer = (byte*)ScreenBitmap.BackBuffer.ToPointer();
            //}
        }

        async Task DecodeThread(Func<(int width, int height)> sizeAccessor)
        {
            using TcpClient client = new TcpClient();
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 5555));
            using NetworkStream stream = client.GetStream();

            using BinaryReader reader = new(stream);
            RdpCodecParameter rcp = RdpCodecParameter.FromSpan(reader.ReadBytes(16));

            using CodecContext cc = new(Codec.FindDecoderById(rcp.CodecId))
            {
                Width = rcp.Width,
                Height = rcp.Height,
                PixelFormat = rcp.PixelFormat,
            };
            cc.Open(null);

            foreach (var frame in reader.ReadPackets()
                .DecodePackets(cc)
                .ConvertVideoFrames(sizeAccessor, AVPixelFormat.Bgra)
                .ToManaged()
                )
            {
                if (!IsRender) break;
                //managedFrame = frame;


                System.Windows.Application.Current.Dispatcher.Invoke(() =>
               {
                   try
                   {
                       _screenBitmap.Lock();
                       Marshal.Copy(frame.Data, 0, ScreenBitmap.BackBuffer, frame.Length);
                       _screenBitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, 1920, 1080));
                   }
                   catch (Exception e)
                   {
                   }
                   finally { _screenBitmap.Unlock(); }
               });

            }
        }


    }


    public static class FramesExtensions
    {
        public static IEnumerable<ManagedBgraFrame> ToManaged(this IEnumerable<Frame> bgraFrames, bool unref = true)
        {
            foreach (Frame frame in bgraFrames)
            {
                int rowPitch = frame.Linesize[0];
                int length = rowPitch * frame.Height;
                byte[] buffer = new byte[length];
                Marshal.Copy(frame.Data._0, buffer, 0, length);
                ManagedBgraFrame managed = new(buffer, length, length / frame.Height);
                if (unref) frame.Unref();
                yield return managed;
            }
        }
    }

    public record struct ManagedBgraFrame(byte[] Data, int Length, int RowPitch)
    {
        public int Width => RowPitch / BytePerPixel;
        public int Height => Length / RowPitch;

        public const int BytePerPixel = 4;
    }


    public static class ReadPacketExtensions
    {
        public static IEnumerable<Packet> ReadPackets(this BinaryReader reader)
        {
            using Packet packet = new();
            while (true)
            {
                int packetSize = reader.ReadInt32();
                if (packetSize == 0) yield break;

                byte[] data = reader.ReadBytes(packetSize);
                GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    packet.Data = new DataPointer(dataHandle.AddrOfPinnedObject(), packetSize);
                    yield return packet;
                }
                finally
                {
                    dataHandle.Free();
                }
            }
        }
    }

    record RdpCodecParameter(AVCodecID CodecId, int Width, int Height, AVPixelFormat PixelFormat)
    {
        public static RdpCodecParameter FromSpan(ReadOnlySpan<byte> data)
        {
            return new RdpCodecParameter(
                CodecId: (AVCodecID)BinaryPrimitives.ReadInt32LittleEndian(data),
                Width: BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
                Height: BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
                PixelFormat: (AVPixelFormat)BinaryPrimitives.ReadInt32LittleEndian(data[12..]));
        }
    }
}
