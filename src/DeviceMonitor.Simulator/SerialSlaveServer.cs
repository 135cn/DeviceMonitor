using DeviceMonitor.Core.Protocol;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Simulator
{
    /// <summary>
    /// 串口从站外壳：串口收到的字节流 → <see cref="FrameAssembler"/> 切成完整帧
    /// → <see cref="ModbusRtuSlave"/> 处理 → 响应写回串口。
    ///
    /// 协议逻辑全部在 Core，本类只负责串口 IO 与日志，因此不承载任何 Modbus 语义。
    /// </summary>
    public sealed class SerialSlaveServer : IDisposable
    {
        private readonly SerialPort _port;
        private readonly ModbusRtuSlave _slave;
        private readonly bool _verbose;
        private readonly Action<string> _log;

        /// <param name="verbose">true 时打印收发的原始 HEX（排查波特率不一致的利器）。</param>
        /// <param name="log">日志输出，默认控制台；测试时可注入收集器。</param>
        public SerialSlaveServer(
            string portName,
            int baudRate,
            ModbusRtuSlave slave,
            bool verbose = false,
            Action<string>? log = null)
        {
            _slave = slave;
            _verbose = verbose;
            _log = log ?? Console.WriteLine;

            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                // 读超时设短一些：既能让循环及时响应取消，也不会因为长时间阻塞而刷不了波形
                ReadTimeout = 50,
                WriteTimeout = 500,
            };


        }

        public string PortName => _port.PortName;

        public void Open() => _port.Open();

        /// <summary>
        /// 阻塞式服务循环，直到 <paramref name="token"/> 被取消。
        /// 端口被拔出等 IO 故障会抛 <see cref="IOException"/>，由调用方决定是否退出进程。
        /// </summary>
        public void Run(CancellationToken token)
        {
            var assembler = new FrameAssembler(
                FrameDirection.SlaveRequest,
                frameGap: FrameAssembler.FrameGapFor(_port.BaudRate));

            var buffer = new byte[256];

            while (!token.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = _port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (count <= 0)
                    continue;

                if (_verbose)
                    _log($"[RX] {Convert.ToHexString(buffer.AsSpan(0, count))}");

                assembler.Feed(buffer.AsSpan(0, count));

                while(assembler.TryGetFrame(out byte[]? frame) && frame is not null)
                {
                    byte[]? response = _slave.HandleRequest(frame);

                    if(response is null)
                    {
                        if (_verbose)
                            _log($"[--] 忽略：{Convert.ToHexString(frame)}（CRC 错 / 非本从站地址）");

                        continue;
                    }
                    _port.Write(response, 0, response.Length);

                    if (_verbose)
                        _log($"[TX] {Convert.ToHexString(response)}");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if(_port.IsOpen)
                    _port.Close();
            }
            catch (IOException)
            {
                // 端口已被拔出：关闭失败可以忽略
            }

            _port.Dispose();
        }
    }
}
