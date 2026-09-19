using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceMonitor.Core.Protocol
{

    /// <summary>Modbus 异常响应的异常码（响应帧里 功能码|0x80 后面那一个字节）。</summary>
    public enum ModbusExceptionCode : byte
    {
        /// <summary>非法功能码：从站不支持该功能。</summary>
        IllegalFunction = 0x01,

        /// <summary>非法数据地址：地址 + 数量超出从站寄存器范围。</summary>
        IllegalDataAddress = 0x02,

        /// <summary>非法数据值：数量、字节数等参数不合法。</summary>
        IllegalDataValue = 0x03,

        /// <summary>从站设备故障：从站内部错误。</summary>
        SlaveDeviceFailure = 0x04,

    }

    /// <summary>异常码 → 中文说明（日志 / 界面显示用，主站从站两侧共用）。</summary>
    public static class ModbusExceptionDescriptions
    {
        public static string Describe(ModbusExceptionCode code) => Describe((byte)code);

        public static string Describe(byte code) => code switch
        {
            (byte)ModbusExceptionCode.IllegalFunction => "非法功能码",
            (byte)ModbusExceptionCode.IllegalDataAddress => "非法数据地址",
            (byte)ModbusExceptionCode.IllegalDataValue => "非法数据值",
            (byte)ModbusExceptionCode.SlaveDeviceFailure => "从站设备故障",
            _ => $"未知异常码 0x{code:X2}",
        };
    }
}
