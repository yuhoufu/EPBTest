using System;
using System.IO.Ports;

namespace Controller.Alarm
{
    internal interface IAlarmPanelTransport : IDisposable
    {
        bool IsOpen { get; }
        void Open();
        void Send(byte[] frame, bool expectResponse);
    }

    internal sealed class M7055dSerialClient : IAlarmPanelTransport
    {
        private readonly SerialPort _port;

        public M7055dSerialClient(string portName, int baud, int dataBits, Parity parity, StopBits stopBits,
            int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("PortName is required.", nameof(portName));

            _port = new SerialPort(portName, baud, parity, dataBits, stopBits)
            {
                ReadTimeout = Math.Max(50, Math.Min(1000, timeoutMs)),
                WriteTimeout = Math.Max(50, Math.Min(1000, timeoutMs))
            };
        }

        public bool IsOpen => _port.IsOpen;

        public void Open()
        {
            if (_port.IsOpen) return;
            _port.Open();
        }

        public void Close()
        {
            if (!_port.IsOpen) return;
            _port.Close();
        }

        public void Send(byte[] frame, bool expectResponse)
        {
            if (frame == null || frame.Length == 0)
                throw new ArgumentException("Frame is empty.", nameof(frame));

            if (!_port.IsOpen)
                _port.Open();

            try
            {
                _port.DiscardInBuffer();
            }
            catch
            {
                // ignore
            }

            _port.Write(frame, 0, frame.Length);

            if (!expectResponse) return;

            // M-7055D 在现场可能会返回，也可能不返回；
            // 这里做一次尽力读取（超时即放弃），不校验内容。
            try
            {
                var buf = new byte[256];
                _ = _port.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch
            {
                // ignore
            }

            _port.Dispose();
        }
    }
}
