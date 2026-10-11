namespace HalconVision.Application;

public static class BuiltInNodeCatalog
{
    public static NodeRegistry Create()
    {
        var registry = new NodeRegistry();
        registry.Register(new BuiltInTestNodeProvider(),
        [
            new ConstantTextExecutor(),
            new PassThroughExecutor(),
            new LogValueExecutor()
        ]);
        return registry;
    }
}
