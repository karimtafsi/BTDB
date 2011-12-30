using System;
using BTDB.IOC.CRegs;

namespace BTDB.IOC
{
    internal class SingleFactoryRegistration : RegistrationBaseImpl<IAsTraitAndLiveScopeTrait>, IContanerRegistration
    {
        readonly Type _implementationType;
        readonly AsTraitImpl _asTrait = new AsTraitImpl();
        readonly LiveScopeTraitImpl _liveScopeTrait = new LiveScopeTraitImpl();
        readonly Func<IContainer, object> _factory;

        public SingleFactoryRegistration(Func<IContainer, object> factory, Type type)
        {
            _factory = factory;
            _implementationType = type;
        }

        public void Register(ContanerRegistrationContext context)
        {
            ICRegILGen reg = new FactoryImpl(context.AddInstance(_factory), _implementationType);
            if (_liveScopeTrait.Lifetime == Lifetime.Singleton)
            {
                reg = new SingletonImpl(_implementationType, reg, context.SingletonCount);
                context.SingletonCount++;
            }
            context.AddCReg(_asTrait.GetAsTypesFor(_implementationType), (ICReg)reg);
        }

        public override object InternalTraits(Type trait)
        {
            if (trait == typeof(IAsTrait)) return _asTrait;
            if (trait == typeof(ILiveScopeTrait)) return _liveScopeTrait;
            throw new ArgumentOutOfRangeException();
        }
    }
}