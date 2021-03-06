﻿namespace Incubator.RpcContract
{
    public interface IDataContract
    {
        // 使用int32的时候，在rpc服务端反射调用时总是会报告32转64类型转换异常，
        // 要么是client序列化时4字节搞成了8字节，要么是server反序列化时4字节搞成了8字节
        // 暂时先用long了
        long AddMoney(long input1, long input2);
    }
}
