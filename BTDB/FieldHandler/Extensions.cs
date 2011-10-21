using System;
using System.Reflection.Emit;
using BTDB.IL;
using BTDB.StreamLayer;

namespace BTDB.FieldHandler
{
    public static class Extensions
    {
        static public void WriteFieldHandler(this AbstractBufferedWriter writer, IFieldHandler handler)
        {
            writer.WriteString(handler.Name);
            writer.WriteByteArray(handler.Configuration);
        }

        static public IFieldHandler CreateFromReader(this IFieldHandlerFactory factory,AbstractBufferedReader reader)
        {
            var handlerName = reader.ReadString();
            var handlerConfiguration = reader.ReadByteArray();
            return factory.CreateFromName(handlerName, handlerConfiguration);
        }

        public static ILGenerator GenerateLoad(this ILGenerator ilGenerator, IFieldHandler fieldHandler, Type typeWanted, Action<ILGenerator> pushReaderOrCtx, ITypeConvertorGenerator typeConvertorGenerator)
        {
            fieldHandler.Load(ilGenerator,
                              fieldHandler.NeedsCtx() ? pushReaderOrCtx : PushReaderFromCtx(pushReaderOrCtx));
            typeConvertorGenerator.GenerateConversion(fieldHandler.HandledType(), typeWanted)(ilGenerator);
            return ilGenerator;
        }

        public static ILGenerator GenerateSkip(this ILGenerator ilGenerator, IFieldHandler fieldHandler, Action<ILGenerator> pushReaderOrCtx)
        {
            fieldHandler.Skip(ilGenerator,
                                  fieldHandler.NeedsCtx() ? pushReaderOrCtx : PushReaderFromCtx(pushReaderOrCtx));
            return ilGenerator;
        }

        public static Action<ILGenerator> PushReaderFromCtx(Action<ILGenerator> pushReaderOrCtx)
        {
            return il => { pushReaderOrCtx(il); il.Callvirt(() => ((IReaderCtx)null).Reader()); };
        }

        static public Action<ILGenerator> PushWriterOrCtxAsNeeded(Action<ILGenerator> pushWriterOrCtx, bool noConversion)
        {
            return noConversion ? pushWriterOrCtx : PushWriterFromCtx(pushWriterOrCtx);
        }

        static public Action<ILGenerator> PushWriterFromCtx(Action<ILGenerator> pushWriterOrCtx)
        {
            return il => { pushWriterOrCtx(il); il.Callvirt(() => ((IWriterCtx)null).Writer()); };
        }
    }
}