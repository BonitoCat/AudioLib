namespace AudioLib.Output;

internal interface IAudioOutput : IDisposable
{
    void Write(short[] samples, int count);
    void Flush();
}
