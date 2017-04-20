using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XnapBox
{
    class Program
    {
        static XBDataReceiver _xb;
        static String xnapboxIP = "192.168.99.1";
        static int xnapboxPort = 8080;

        static void Main(string[] args)
        {
            _xb = new XBDataReceiver();
            _xb.FrameReady += xb_FrameReady;
            _xb.HeartbeatReady += _xb_HeartbeatReady;
            _xb.Error += new EventHandler<XnapBox.ErrorEventArgs>(_xb_Error);

            _xb.ParseStream(new Uri("http://" + xnapboxIP + ":" + xnapboxPort));

            Console.WriteLine("Press any key to stop...");
            Console.ReadKey();

            _xb.StopStream();
        }

        private static void _xb_HeartbeatReady(object sender, HeartbeatEventArgs e)
        {
            Console.Write("Heartbeat");
        }

        private static void xb_FrameReady(object sender, FrameReadyEventArgs e)
        {
            try
            {
                Console.Write(e.toString());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        private static void _xb_Error(object sender, XnapBox.ErrorEventArgs e)
        {
            Console.Write("Reconnect");
            _xb.StopStream();
            _xb.ParseStream(new Uri("http://" + xnapboxIP + ":" + xnapboxPort));
        }
    }
}
