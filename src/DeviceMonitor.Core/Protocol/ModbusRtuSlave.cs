namespace DeviceMonitor.Core.Protocol
{
    /// <summary>
    /// Modbus RTU 从站逻辑（纯内存，不碰串口）：维护寄存器区，按请求构造响应。
    /// 串口收发由 DeviceMonitor.Simulator 的 SerialSlaveServer 负责，因此本类完全可单元测试。
    /// </summary>
    public sealed class ModbusRtuSlave
    {
        private readonly ushort[] _holdingRegisters;
        private readonly ushort[] _inputRegisters;

        public ModbusRtuSlave(byte slaveId, int registerCount = 100)
        {
            if (slaveId is 0 or > 247)
                throw new ArgumentOutOfRangeException(
                    nameof(slaveId), slaveId, "从站地址必须在 1~247 之间。");

            if (registerCount < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(registerCount), registerCount, "寄存器数量必须大于 0。");

            SlaveId = slaveId;
            _holdingRegisters = new ushort[registerCount];
            _inputRegisters = new ushort[registerCount];

        }

        public byte SlaveId { get; }


        /// <summary>保持寄存器区（FC 03 读，06/10 写）。模拟器用它定时刷新波形数据。</summary>
        public Span<ushort> HoldingRegisters => _holdingRegisters;

        /// <summary>输入寄存器区（FC 04 只读）。模拟器用它定时刷新波形数据。</summary>
        public Span<ushort> InputRegisters => _inputRegisters;

        /// <summary>成功响应次数（控制台打印用）。</summary>
        public long HandledCount { get; private set; }

        /// <summary>异常响应次数。</summary>
        public long ExceptionResponseCount { get; private set; }

        /// <summary>CRC 或结构不合法、被静默丢弃的帧数。</summary>
        public long DiscardedFrameCount { get; private set; }

        /// <summary>不是发给本从站、被忽略的帧数（总线上其它从站的请求）。</summary>
        public long IgnoredForOtherSlaveCount { get; private set; }

        /// <summary>
        /// 处理一帧请求，返回需要发送的响应帧。
        /// 返回 null 表示"不响应"：帧不合法（CRC / 结构），或地址不是发给本从站的。
        /// </summary>
        public byte[]? HandleRequest(ReadOnlySpan<byte> request)
        {
            if (!ModbusRtuCodec.TryParseRequest(request, out ModbusRequest? parsed) || parsed is null)
            {
                DiscardedFrameCount++;
                return null;
            }

            if (parsed.SlaveId != SlaveId)
            {
                IgnoredForOtherSlaveCount++;
                return null;
            }

            return Dispatch(parsed);
        }

        private byte[] Dispatch(ModbusRequest request) => request.FunctionCode switch
        {
            0x03 => ReadRegisters(_holdingRegisters, request),
            0x04 => ReadRegisters(_inputRegisters, request),
            0x06 => WriteSingleRegisters(request),
            0x10 => WriteMultipleRegisters(request),
            _ => Exception(request, ModbusExceptionCode.IllegalFunction),

        };

        private byte[] WriteMultipleRegisters(ModbusRequest request)
        {
            // 0x10 的协议上限是 123 个寄存器；同时确认解析出的值与数量自洽
            if (request.Quantity is 0 or > 123 || request.Data.Length != request.Quantity)
                return Exception(request, ModbusExceptionCode.IllegalDataValue);
            if (!IsRangeValid(_holdingRegisters, request.StartAddress, request.Quantity))
                return Exception(request, ModbusExceptionCode.IllegalDataAddress);

            for (int i = 0; i < request.Quantity; i++)
                _holdingRegisters[request.StartAddress + i] = request.Data[i];

            HandledCount++;
            return ModbusRtuCodec.BuildWriteResponse(SlaveId, request.FunctionCode, request.StartAddress, request.Quantity);
        }

        private byte[] WriteSingleRegisters(ModbusRequest request)
        {
            if (request.Data.Length != 1)
                return Exception(request, ModbusExceptionCode.IllegalDataValue);

            if (!IsRangeValid(_holdingRegisters, request.StartAddress, 1))
                return Exception(request, ModbusExceptionCode.IllegalDataAddress);

            _holdingRegisters[request.StartAddress] = request.Data[0];

            HandledCount++;
            // 06 的响应 = 回显请求：第 4 个 16 位字段必须是「写入的值」，不是数量
            return ModbusRtuCodec.BuildWriteResponse(SlaveId, request.FunctionCode, request.StartAddress, request.Data[0]);
        }

        private byte[] ReadRegisters(ushort[] area, ModbusRequest request)
        {
            if (request.Quantity is 0 or > ModbusRtuCodec.MaxReadQuantity)
                return Exception(request, ModbusExceptionCode.IllegalDataValue);
            if (!IsRangeValid(area, request.StartAddress, request.Quantity))
                return Exception(request, ModbusExceptionCode.IllegalDataAddress);

            HandledCount++;
            return ModbusRtuCodec.BuildReadResponse(
                SlaveId, request.FunctionCode, area.AsSpan(request.StartAddress, request.Quantity));
        }

        private static bool IsRangeValid(ushort[] area, ushort startAddress, int quantity)
            => (int)startAddress + quantity <= area.Length;


        private byte[] Exception(ModbusRequest request, ModbusExceptionCode code)
        {
            ExceptionResponseCount++;
            return ModbusRtuCodec.BuildExceptionResponse(SlaveId, request.FunctionCode, code);
        }
    }
}
