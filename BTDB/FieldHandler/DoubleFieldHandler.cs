﻿using System;
using BTDB.IL;
using BTDB.StreamLayer;

namespace BTDB.FieldHandler
{
    public class DoubleFieldHandler : SimpleFieldHandlerBase
    {
        public DoubleFieldHandler(): base(
            EmitHelpers.GetMethodInfo(() => default(AbstractBufferedReader).ReadDouble()),
            EmitHelpers.GetMethodInfo(() => default(AbstractBufferedReader).SkipDouble()),
            EmitHelpers.GetMethodInfo(() => default(AbstractBufferedWriter).WriteDouble(0)))
        {
        }

        public override string Name
        {
            get { return "Double"; }
        }

        public override bool IsCompatibleWith(Type type, FieldHandlerOptions options)
        {
            return type == typeof(double) || type == typeof(float);
        }
    }
}