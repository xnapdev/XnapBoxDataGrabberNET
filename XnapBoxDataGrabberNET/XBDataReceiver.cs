using System;
using System.Text;
using System.Net;
using System.IO;
using System.Collections.Generic;

using System.Threading;
using System.Drawing;
using System.Linq;

namespace XnapBox
{
	internal class XBDataReceiver
	{
		// WinForms & WPF
		public Bitmap Bitmap { get; set; }

		// magic 2 byte header for JPEG images
		private readonly byte[] JpegHeader = new byte[] { 0xff, 0xd8 };

		// pull down 1024 bytes at a time
		private const int ChunkSize = 1024;

		// used to cancel reading the stream
		private bool _streamActive;
        private bool _isClosed = true;

		// current encoded JPEG image
		public byte[] CurrentFrame { get; private set; }

		// used to marshal back to UI thread
        //private SynchronizationContext _context;

		// event to get the buffer above handed to you
		public event EventHandler<FrameReadyEventArgs> FrameReady;
        public event EventHandler<FullFrameReadyEventArgs> FullFrameReady;
        public event EventHandler<HeartbeatEventArgs> HeartbeatReady;
		public event EventHandler<ErrorEventArgs> Error;

        private Uri uri;
        private int readWriteTimeout = 2000;

        public XBDataReceiver()
        {

        }

        public void setTimeout(int millseconds)
        {
            readWriteTimeout = millseconds;
        }

		public void ParseStream(Uri uri)
		{
			ParseStream(uri, null, null);
		}

		public void ParseStream(Uri uri, string username, string password)
		{
            Console.WriteLine("ParseStream: " + uri.AbsoluteUri);
            this.uri = uri;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
			if(!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
				request.Credentials = new NetworkCredential(username, password);

            genTIDPrefix();

            request.Timeout = 30000;
            request.ReadWriteTimeout = readWriteTimeout;
			// asynchronously get a response
			request.BeginGetResponse(OnGetResponse, request);
		}

		public void StopStream()
		{
			_streamActive = false;
            //while (!_isClosed)
            //{
            //    Thread.Sleep(100);
            //}
		}

		private void OnGetResponse(IAsyncResult asyncResult)
		{
            Console.WriteLine("OnGetResponse [begin]");

			byte[] imageBuffer = new byte[1024 * 1024];

			// get the response
			HttpWebRequest req = (HttpWebRequest)asyncResult.AsyncState;
            HttpWebResponse resp = null;

            try
            {
                //logger.Debug("OnGetResponse 1");
                resp = (HttpWebResponse)req.EndGetResponse(asyncResult);

                // find our magic boundary value
                string contentType = resp.Headers["Content-Type"];
                if (!string.IsNullOrEmpty(contentType) && !contentType.Contains("="))
                    throw new Exception("Invalid content-type header.  The camera is likely not returning a proper MJPEG stream.");
                string boundary = resp.Headers["Content-Type"].Split('=')[1].Replace("\"", "");
                byte[] boundaryBytes = Encoding.UTF8.GetBytes(boundary.StartsWith("--") ? boundary : "--" + boundary);
                //logger.Debug(Encoding.UTF8.GetString(boundaryBytes, 0, boundaryBytes.Length));

                //logger.Debug("OnGetResponse 2");
                Stream s = resp.GetResponseStream();
                BinaryReader br = new BinaryReader(s);

                _streamActive = true;
                _isClosed = false;

                //logger.Debug("OnGetResponse 3");
                byte[] buff = br.ReadBytes(ChunkSize);
                //logger.Debug(Encoding.UTF8.GetString(buff, 0, buff.Length));

                //logger.Debug("OnGetResponse 4");
                while (_streamActive)
                {
                    //logger.Debug("OnGetResponse 5");
                    // find the JPEG header
                    int imageStart = buff.Find(JpegHeader);

                    if (imageStart != -1)
                    {
                        //logger.Debug("OnGetResponse 6");
                        String frameHeader = Encoding.UTF8.GetString(buff, 0, imageStart).Replace(boundary, "");
                        // copy the start of the JPEG image to the imageBuffer
                        int size = buff.Length - imageStart;
                        Array.Copy(buff, imageStart, imageBuffer, 0, size);

                        while (true)
                        {
                            //logger.Debug("OnGetResponse 7");
                            buff = br.ReadBytes(ChunkSize);
                            //logger.Debug("OnGetResponse 8");

                            // find the boundary text
                            int imageEnd = buff.Find(boundaryBytes);
                            //logger.Debug("OnGetResponse 9");
                            if (imageEnd != -1)
                            {
                                // copy the remainder of the JPEG to the imageBuffer
                                Array.Copy(buff, 0, imageBuffer, size, imageEnd);
                                size += imageEnd;

                                byte[] frame = new byte[size];
                                Array.Copy(imageBuffer, 0, frame, 0, size);

                                ProcessFrame(frameHeader, frame);

                                // copy the leftover data to the start
                                Array.Copy(buff, imageEnd, buff, 0, buff.Length - imageEnd);

                                // fill the remainder of the buffer with new data and start over
                                byte[] temp = br.ReadBytes(imageEnd);

                                Array.Copy(temp, 0, buff, buff.Length - imageEnd, temp.Length);
                                break;
                            }

                            //logger.Debug("OnGetResponse 10");

                            // copy all of the data to the imageBuffer
                            Array.Copy(buff, 0, imageBuffer, size, buff.Length);
                            size += buff.Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("OnGetResponse [error] " + ex.Message + " (" + uri.AbsoluteUri + ")");
                if (Error != null)
                    //_context.Post(delegate { 
                    Error(this, new ErrorEventArgs() { Message = ex.Message });
                //}, null);
                return;
            }
            finally
            {
                Console.WriteLine("OnGetResponse resp.Close()");
                if (resp != null)
                    resp.Close();
            }
            _isClosed = true;
            Console.WriteLine("OnGetResponse [end]");
		}

        private String safeGetString(Dictionary<string, string> dic, String key, String defaultValue)
        {
            String ret = defaultValue;
            if (dic.ContainsKey(key))
            {
                ret = dic[key];
            }
            return ret;
        }

        private int safeGetInt(Dictionary<string, string> dic, String key, int defaultValue)
        {
            int ret = defaultValue;
            if (!int.TryParse(safeGetString(dic, key, defaultValue + ""), out ret))
            {
                ret = defaultValue;
            }
            return ret;
        }

        private double safeGetDouble(Dictionary<string, string> dic, String key, double defaultValue)
        {
            double ret = defaultValue;
            if (!double.TryParse(safeGetString(dic, key, defaultValue + ""), out ret))
            {
                ret = defaultValue;
            }
            return ret;
        }

        private Color parseHSVString(String hsv)
        {
            Color ret = Color.Transparent;
            double h;
            double s;
            double v;
            
            //#999#999#999
            try
            {
                if (hsv.Contains("#"))
                {
                    String[] hsvValues = hsv.Split('#');
                    if (hsvValues.Length >= 4)
                    {
                        double.TryParse(hsvValues[1], out h);
                        double.TryParse(hsvValues[2], out s);
                        double.TryParse(hsvValues[3], out v);
                        Console.WriteLine("parseHSVString h:" + h + ", s:" + s + ", v:" + v);
                        ret = ColorFromHSV(h, s, v);
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Error when converting to RGB color.", e);
            }

            return ret;
        }

        private void ColorToHSV(Color color, out double hue, out double saturation, out double value)
        {
            int max = Math.Max(color.R, Math.Max(color.G, color.B));
            int min = Math.Min(color.R, Math.Min(color.G, color.B));

            hue = color.GetHue();
            saturation = (max == 0) ? 0 : 1d - (1d * min / max);
            value = max / 255d;
        }

        private Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            value = value/100 * 255;
            saturation = saturation / 100;
            int v = Convert.ToInt32(value);
            int p = Convert.ToInt32(value * (1 - saturation));
            int q = Convert.ToInt32(value * (1 - f * saturation));
            int t = Convert.ToInt32(value * (1 - (1 - f) * saturation));

            if (hi == 0)
                return Color.FromArgb(255, v, t, p);
            else if (hi == 1)
                return Color.FromArgb(255, q, v, p);
            else if (hi == 2)
                return Color.FromArgb(255, p, v, t);
            else if (hi == 3)
                return Color.FromArgb(255, p, q, v);
            else if (hi == 4)
                return Color.FromArgb(255, t, p, v);
            else
                return Color.FromArgb(255, v, p, q);
        }

        private int parseTimeStamp(String timeStampStr, out double time)
        {
            time = 0;
            int ret = 0;

            if (!timeStampStr.Contains("-"))
            {
                time = 0;
                return 0;
            }
            String[] tss = timeStampStr.Split('-');
        
            if (tss.Length == 2)
            {
                double.TryParse(tss[0], out time);
                int.TryParse(tss[1], out ret);
            }
            else if (tss.Length == 3)
            {
                String realTime = tss[0];
                String milliseconds = "000000";
                if (tss[1].Contains("."))
                {
                    String[] mms = tss[1].Split('.');
                    milliseconds = mms[1];
                }

                int.TryParse(tss[2], out ret);

                if (realTime.Contains("T"))
                {
                    //logger.Debug(realTime + "." + milliseconds);
                    DateTime d = DateTime.ParseExact(realTime + "." + milliseconds, "yyyyMMddTHHmmss.FFFFFF", System.Globalization.CultureInfo.InvariantCulture);
                    //logger.Debug(d.ToString("yyyy/MM/dd HH:mm:ss.FFFFFF"));
                    DateTime epoch = new DateTime(1970, 1, 1, 8, 0, 0, DateTimeKind.Utc); //TODO XB is hardcode HKT
                    time = (d - epoch).TotalSeconds;
                    Console.WriteLine(time);
                }
            }
            return ret;
        }

        private int prevTrackerID = 0;
        private String formatTrackerID(String trackerID)
        {
            String ret = trackerID;
            int iTrackerID = 0;
            if (int.TryParse(trackerID, out iTrackerID))
            {
                if (prevTrackerID > iTrackerID)
                {
                    genTIDPrefix();
                }
                ret = tidPrefix + trackerID;
            }

            return ret;
        }
        private String tidPrefix = "";
        private void genTIDPrefix()
        {
            tidPrefix = randomString(4);
        }
        private static Random random = new Random();
        public static string randomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }

		private void ProcessFrame(String frameHeaderStr, byte[] frame)
		{
            Console.WriteLine("ProcessFrame [begin]");
			CurrentFrame = frame;

            Dictionary<string, string> frameHeader = extractFrameHeader(frameHeaderStr);
            String timestamp = safeGetString(frameHeader, "X-Timestamp", "");
            double time;
            int frameID = parseTimeStamp(timestamp, out time);

            String filename = safeGetString(frameHeader, "X-File", "");
            int centerY = safeGetInt(frameHeader, "X-objectYpos", 0);
            int centerX = safeGetInt(frameHeader, "X-objectXpos", 0);
            int objectWidth = safeGetInt(frameHeader, "X-objectWidth", 0);
            int objectHeight = safeGetInt(frameHeader, "X-objectHeight", 0);
            String trackerID = safeGetString(frameHeader, "X-TrackerID", "");
            trackerID = formatTrackerID(trackerID);
            String trackerDir = safeGetString(frameHeader, "X-TrackerDir", "");
            String objectColor1HSV = safeGetString(frameHeader, "X-ObjectColor1HSV", "");
            String objectColor2HSV = safeGetString(frameHeader, "X-ObjectColor2HSV", "");
            Color color1 = parseHSVString(objectColor1HSV);
            Color color2 = parseHSVString(objectColor2HSV);
            int blurIndex = safeGetInt(frameHeader, "X-BlurIndex", -1);

            Boolean isFullFrame = false;
            Boolean isHeartbeat = false;

			// create a simple GDI+ happy Bitmap
			Bitmap = new Bitmap(new MemoryStream(frame));
            if (Bitmap.Width == 1 && Bitmap.Height == 1)
            {
                isHeartbeat = true;
            }
            //if (filename.Contains("-TEST-"))
            //{
            //    isFullFrame = true;
            //}


            if (isHeartbeat)
            {
                Console.WriteLine("ProcessFrame heartbeat");
                if (HeartbeatReady != null)
                    HeartbeatReady(this, new HeartbeatEventArgs { timestamp = timestamp });
            }
            else if (isFullFrame)
            {
                Console.WriteLine("ProcessFrame FullFrame");
                if (FullFrameReady != null)
                {
                    try
                    {
                        FullFrameReady(this, new FullFrameReadyEventArgs { timestamp = timestamp, time = time, frameID = frameID, filename = filename, FrameBuffer = CurrentFrame, Bitmap = Bitmap });
                    }
                    catch (Exception )
                    {
                        Console.Error.WriteLine("Unhandled exception on frame ready.");
                    }
                }
            }
            else
            {
                if (FrameReady != null)
                {
                    try
                    {
                        FrameReady(this, new FrameReadyEventArgs { timestamp = timestamp, time = time, frameID = frameID, filename = filename, FrameBuffer = CurrentFrame, Bitmap = Bitmap, centerX = centerX, centerY = centerY, objectHeight = objectHeight, objectWidth = objectWidth, trackerID = trackerID, color1 = color1, color2 = color2, blurIndex = blurIndex });
                    }
                    catch (Exception )
                    {
                        Console.Error.WriteLine("Unhandled exception on frame ready.");
                    }
                }
            }
            Console.WriteLine("ProcessFrame [end]");
		}

        public Dictionary<string, string> extractFrameHeader(String frameHeader)
        {
            Dictionary<string, string> ret = new Dictionary<string, string>();
            //logger.Debug("Frame header: " + frameHeader);

            String[] lines = frameHeader.Split('\n');
            foreach (String line in lines)
            {
                if (line.Contains(":"))
                {
                    string key = line.Split(':')[0].Trim();
                    string value = line.Split(':')[1].Replace("\"", "").Trim();
                    ret.Add(key, value);
                }
            }

            return ret;
        }
	}

    static class Extensions
	{
		public static int Find(this byte[] buff, byte[] search)
		{
			// enumerate the buffer but don't overstep the bounds
			for(int start = 0; start < buff.Length - search.Length; start++)
			{
				// we found the first character
				if(buff[start] == search[0])
				{
					int next;

					// traverse the rest of the bytes
					for(next = 1; next < search.Length; next++)
					{
						// if we don't match, bail
						if(buff[start+next] != search[next])
							break;
					}

					if(next == search.Length)
						return start;
				}
			}
			// not found
			return -1;	
		}
	}

	public class FrameReadyEventArgs : EventArgs
	{
		public byte[] FrameBuffer;
		public Bitmap Bitmap;
        public String timestamp;
        public double time;
        public int frameID;
        public String filename;
        public int centerY;
        public int centerX;
        public int objectWidth;
        public int objectHeight;
        public String trackerID;
        public Color color1;
        public Color color2;
        public int blurIndex;

        public String toString()
        {
            String ret = "Timestamp: " + timestamp + " (" + time + ") ";
            ret += "Center: (" + centerX + ", " + centerY + ") ";
            ret += "Dimension: (" + objectWidth + " x " + objectHeight + ") ";
            ret += "TrackerID: " + trackerID + " ";
            ret += "frameID: " + frameID + " ";
            ret += "Color: " + color1 + " " + color2 + " ";
            ret += "FrameBuffer.length: " + FrameBuffer.Length + " ";

            return ret;
        }
	}

    public class FullFrameReadyEventArgs : EventArgs
    {
        public byte[] FrameBuffer;
        public Bitmap Bitmap;
        public String timestamp;
        public double time;
        public int frameID;
        public String filename;
    }

    public class HeartbeatEventArgs : EventArgs
    {
        public String timestamp;
    }

	public sealed class ErrorEventArgs : EventArgs
	{
		public string Message { get; set; }
		public int ErrorCode { get; set; }
	}
}
