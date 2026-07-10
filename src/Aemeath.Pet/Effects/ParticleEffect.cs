namespace Aemeath.Pet.Effects;

/// <summary>兼容现有窗口生命周期的粒子控制器。</summary>
public sealed class ParticleEffect
{
    private readonly ParticleField _field;

    public ParticleEffect(ParticleField field)
    {
        _field = field;
    }

    public void Start(int particleCount = 30)
    {
        _field.Start(particleCount);
    }

    public void Stop()
    {
        _field.Stop();
    }
}
