namespace SharpLink.Abstractions;


//程序集通过动态生成的类型实现该接口来动态注册RpcService
public interface IServiceRegister
{
    void ConfigureServices(IServiceCollection services);
}