﻿using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Neo.SmartContract
{
    public class StandardService : IDisposable, IInteropService
    {
        public static event EventHandler<NotifyEventArgs> Notify;
        public static event EventHandler<LogEventArgs> Log;

        public readonly TriggerType Trigger;
        internal readonly Snapshot Snapshot;
        protected readonly List<IDisposable> Disposables = new List<IDisposable>();
        protected readonly Dictionary<UInt160, UInt160> ContractsCreated = new Dictionary<UInt160, UInt160>();
        private readonly List<NotifyEventArgs> notifications = new List<NotifyEventArgs>();
        private readonly Dictionary<uint, Func<ApplicationEngine, bool>> methods = new Dictionary<uint, Func<ApplicationEngine, bool>>();
        private readonly Dictionary<uint, long> prices = new Dictionary<uint, long>();

        public IReadOnlyList<NotifyEventArgs> Notifications => notifications;

        public StandardService(TriggerType trigger, Snapshot snapshot)
        {
            this.Trigger = trigger;
            this.Snapshot = snapshot;
            Register("System.ExecutionEngine.GetScriptContainer", ExecutionEngine_GetScriptContainer, 1);
            Register("System.ExecutionEngine.GetExecutingScriptHash", ExecutionEngine_GetExecutingScriptHash, 1);
            Register("System.ExecutionEngine.GetCallingScriptHash", ExecutionEngine_GetCallingScriptHash, 1);
            Register("System.ExecutionEngine.GetEntryScriptHash", ExecutionEngine_GetEntryScriptHash, 1);
            Register("System.Runtime.Platform", Runtime_Platform, 1);
            Register("System.Runtime.GetTrigger", Runtime_GetTrigger, 1);
            Register("System.Runtime.CheckWitness", Runtime_CheckWitness, 200);
            Register("System.Runtime.Notify", Runtime_Notify, 1);
            Register("System.Runtime.Log", Runtime_Log, 1);
            Register("System.Runtime.GetTime", Runtime_GetTime, 1);
            Register("System.Runtime.Serialize", Runtime_Serialize, 1);
            Register("System.Runtime.Deserialize", Runtime_Deserialize, 1);
            Register("System.Blockchain.GetHeight", Blockchain_GetHeight, 1);
            Register("System.Blockchain.GetHeader", Blockchain_GetHeader, 100);
            Register("System.Blockchain.GetBlock", Blockchain_GetBlock, 200);
            Register("System.Blockchain.GetTransaction", Blockchain_GetTransaction, 200);
            Register("System.Blockchain.GetTransactionHeight", Blockchain_GetTransactionHeight, 100);
            Register("System.Blockchain.GetContract", Blockchain_GetContract, 100);
            Register("System.Header.GetIndex", Header_GetIndex, 1);
            Register("System.Header.GetHash", Header_GetHash, 1);
            Register("System.Header.GetPrevHash", Header_GetPrevHash, 1);
            Register("System.Header.GetTimestamp", Header_GetTimestamp, 1);
            Register("System.Block.GetTransactionCount", Block_GetTransactionCount, 1);
            Register("System.Block.GetTransactions", Block_GetTransactions, 1);
            Register("System.Block.GetTransaction", Block_GetTransaction, 1);
            Register("System.Transaction.GetHash", Transaction_GetHash, 1);
            Register("System.Contract.Call", Contract_Call, 10);
            Register("System.Contract.Destroy", Contract_Destroy, 1);
            Register("System.Contract.GetStorageContext", Contract_GetStorageContext, 1);
            Register("System.Storage.GetContext", Storage_GetContext, 1);
            Register("System.Storage.GetReadOnlyContext", Storage_GetReadOnlyContext, 1);
            Register("System.Storage.Get", Storage_Get, 100);
            Register("System.Storage.Put", Storage_Put);
            Register("System.Storage.PutEx", Storage_PutEx);
            Register("System.Storage.Delete", Storage_Delete, 100);
            Register("System.StorageContext.AsReadOnly", StorageContext_AsReadOnly, 1);
        }

        internal bool CheckStorageContext(StorageContext context)
        {
            ContractState contract = Snapshot.Contracts.TryGet(context.ScriptHash);
            if (contract == null) return false;
            if (!contract.HasStorage) return false;
            return true;
        }

        public void Commit()
        {
            Snapshot.Commit();
        }

        public void Dispose()
        {
            foreach (IDisposable disposable in Disposables)
                disposable.Dispose();
            Disposables.Clear();
        }

        public long GetPrice(uint hash)
        {
            prices.TryGetValue(hash, out long price);
            return price;
        }

        bool IInteropService.Invoke(byte[] method, ExecutionEngine engine)
        {
            uint hash = method.Length == 4
                ? BitConverter.ToUInt32(method, 0)
                : Encoding.ASCII.GetString(method).ToInteropMethodHash();
            if (!methods.TryGetValue(hash, out Func<ApplicationEngine, bool> func)) return false;
            return func((ApplicationEngine)engine);
        }

        protected void Register(string method, Func<ApplicationEngine, bool> handler)
        {
            methods.Add(method.ToInteropMethodHash(), handler);
        }

        protected void Register(string method, Func<ApplicationEngine, bool> handler, long price)
        {
            Register(method, handler);
            prices.Add(method.ToInteropMethodHash(), price);
        }

        protected bool ExecutionEngine_GetScriptContainer(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(engine.ScriptContainer));
            return true;
        }

        protected bool ExecutionEngine_GetExecutingScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CurrentContext.ScriptHash);
            return true;
        }

        protected bool ExecutionEngine_GetCallingScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CurrentContext.CallingScriptHash ?? new byte[0]);
            return true;
        }

        protected bool ExecutionEngine_GetEntryScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.EntryScriptHash);
            return true;
        }

        protected bool Runtime_Platform(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(Encoding.ASCII.GetBytes("NEO"));
            return true;
        }

        protected bool Runtime_GetTrigger(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push((int)Trigger);
            return true;
        }

        internal bool CheckWitness(ExecutionEngine engine, UInt160 hash)
        {
            IVerifiable container = (IVerifiable)engine.ScriptContainer;
            UInt160[] _hashes_for_verifying = container.GetScriptHashesForVerifying(Snapshot);
            return _hashes_for_verifying.Contains(hash);
        }

        protected bool CheckWitness(ExecutionEngine engine, ECPoint pubkey)
        {
            return CheckWitness(engine, Contract.CreateSignatureRedeemScript(pubkey).ToScriptHash());
        }

        protected bool Runtime_CheckWitness(ExecutionEngine engine)
        {
            byte[] hashOrPubkey = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            bool result;
            if (hashOrPubkey.Length == 20)
                result = CheckWitness(engine, new UInt160(hashOrPubkey));
            else if (hashOrPubkey.Length == 33)
                result = CheckWitness(engine, ECPoint.DecodePoint(hashOrPubkey, ECCurve.Secp256r1));
            else
                return false;
            engine.CurrentContext.EvaluationStack.Push(result);
            return true;
        }

        internal void SendNotification(ExecutionEngine engine, UInt160 script_hash, StackItem state)
        {
            NotifyEventArgs notification = new NotifyEventArgs(engine.ScriptContainer, script_hash, state);
            Notify?.Invoke(this, notification);
            notifications.Add(notification);
        }

        protected bool Runtime_Notify(ExecutionEngine engine)
        {
            SendNotification(engine, new UInt160(engine.CurrentContext.ScriptHash), engine.CurrentContext.EvaluationStack.Pop());
            return true;
        }

        protected bool Runtime_Log(ExecutionEngine engine)
        {
            string message = Encoding.UTF8.GetString(engine.CurrentContext.EvaluationStack.Pop().GetByteArray());
            Log?.Invoke(this, new LogEventArgs(engine.ScriptContainer, new UInt160(engine.CurrentContext.ScriptHash), message));
            return true;
        }

        protected bool Runtime_GetTime(ExecutionEngine engine)
        {
            if (Snapshot.PersistingBlock == null)
            {
                Header header = Snapshot.GetHeader(Snapshot.CurrentBlockHash);
                engine.CurrentContext.EvaluationStack.Push(header.Timestamp + Blockchain.SecondsPerBlock);
            }
            else
            {
                engine.CurrentContext.EvaluationStack.Push(Snapshot.PersistingBlock.Timestamp);
            }
            return true;
        }

        protected bool Runtime_Serialize(ExecutionEngine engine)
        {
            byte[] serialized;
            try
            {
                serialized = engine.CurrentContext.EvaluationStack.Pop().Serialize();
            }
            catch (NotSupportedException)
            {
                return false;
            }
            if (serialized.Length > engine.MaxItemSize)
                return false;
            engine.CurrentContext.EvaluationStack.Push(serialized);
            return true;
        }

        protected bool Runtime_Deserialize(ExecutionEngine engine)
        {
            StackItem item;
            try
            {
                item = engine.CurrentContext.EvaluationStack.Pop().GetByteArray().DeserializeStackItem(engine.MaxArraySize);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            engine.CurrentContext.EvaluationStack.Push(item);
            return true;
        }

        protected bool Blockchain_GetHeight(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(Snapshot.Height);
            return true;
        }

        protected bool Blockchain_GetHeader(ExecutionEngine engine)
        {
            byte[] data = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            UInt256 hash;
            if (data.Length <= 5)
                hash = Blockchain.Singleton.GetBlockHash((uint)new BigInteger(data));
            else if (data.Length == 32)
                hash = new UInt256(data);
            else
                return false;
            if (hash == null)
            {
                engine.CurrentContext.EvaluationStack.Push(new byte[0]);
            }
            else
            {
                Header header = Snapshot.GetHeader(hash);
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(header));
            }
            return true;
        }

        protected bool Blockchain_GetBlock(ExecutionEngine engine)
        {
            byte[] data = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            UInt256 hash;
            if (data.Length <= 5)
                hash = Blockchain.Singleton.GetBlockHash((uint)new BigInteger(data));
            else if (data.Length == 32)
                hash = new UInt256(data);
            else
                return false;
            if (hash == null)
            {
                engine.CurrentContext.EvaluationStack.Push(new byte[0]);
            }
            else
            {
                Block block = Snapshot.GetBlock(hash);
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(block));
            }
            return true;
        }

        protected bool Blockchain_GetTransaction(ExecutionEngine engine)
        {
            byte[] hash = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            Transaction tx = Snapshot.GetTransaction(new UInt256(hash));
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(tx));
            return true;
        }

        protected bool Blockchain_GetTransactionHeight(ExecutionEngine engine)
        {
            byte[] hash = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            int? height = (int?)Snapshot.Transactions.TryGet(new UInt256(hash))?.BlockIndex;
            engine.CurrentContext.EvaluationStack.Push(height ?? -1);
            return true;
        }

        protected bool Blockchain_GetContract(ExecutionEngine engine)
        {
            UInt160 hash = new UInt160(engine.CurrentContext.EvaluationStack.Pop().GetByteArray());
            ContractState contract = Snapshot.Contracts.TryGet(hash);
            if (contract == null)
                engine.CurrentContext.EvaluationStack.Push(new byte[0]);
            else
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(contract));
            return true;
        }

        protected bool Header_GetIndex(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                BlockBase header = _interface.GetInterface<BlockBase>();
                if (header == null) return false;
                engine.CurrentContext.EvaluationStack.Push(header.Index);
                return true;
            }
            return false;
        }

        protected bool Header_GetHash(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                BlockBase header = _interface.GetInterface<BlockBase>();
                if (header == null) return false;
                engine.CurrentContext.EvaluationStack.Push(header.Hash.ToArray());
                return true;
            }
            return false;
        }

        protected bool Header_GetPrevHash(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                BlockBase header = _interface.GetInterface<BlockBase>();
                if (header == null) return false;
                engine.CurrentContext.EvaluationStack.Push(header.PrevHash.ToArray());
                return true;
            }
            return false;
        }

        protected bool Header_GetTimestamp(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                BlockBase header = _interface.GetInterface<BlockBase>();
                if (header == null) return false;
                engine.CurrentContext.EvaluationStack.Push(header.Timestamp);
                return true;
            }
            return false;
        }

        protected bool Block_GetTransactionCount(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                Block block = _interface.GetInterface<Block>();
                if (block == null) return false;
                engine.CurrentContext.EvaluationStack.Push(block.Transactions.Length);
                return true;
            }
            return false;
        }

        protected bool Block_GetTransactions(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                Block block = _interface.GetInterface<Block>();
                if (block == null) return false;
                if (block.Transactions.Length > engine.MaxArraySize)
                    return false;
                engine.CurrentContext.EvaluationStack.Push(block.Transactions.Select(p => StackItem.FromInterface(p)).ToArray());
                return true;
            }
            return false;
        }

        protected bool Block_GetTransaction(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                Block block = _interface.GetInterface<Block>();
                int index = (int)engine.CurrentContext.EvaluationStack.Pop().GetBigInteger();
                if (block == null) return false;
                if (index < 0 || index >= block.Transactions.Length) return false;
                Transaction tx = block.Transactions[index];
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(tx));
                return true;
            }
            return false;
        }

        protected bool Transaction_GetHash(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                Transaction tx = _interface.GetInterface<Transaction>();
                if (tx == null) return false;
                engine.CurrentContext.EvaluationStack.Push(tx.Hash.ToArray());
                return true;
            }
            return false;
        }

        protected bool Storage_GetContext(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(new StorageContext
            {
                ScriptHash = new UInt160(engine.CurrentContext.ScriptHash),
                IsReadOnly = false
            }));
            return true;
        }

        protected bool Storage_GetReadOnlyContext(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(new StorageContext
            {
                ScriptHash = new UInt160(engine.CurrentContext.ScriptHash),
                IsReadOnly = true
            }));
            return true;
        }

        protected bool Storage_Get(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (!CheckStorageContext(context)) return false;
                byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
                StorageItem item = Snapshot.Storages.TryGet(new StorageKey
                {
                    ScriptHash = context.ScriptHash,
                    Key = key
                });
                engine.CurrentContext.EvaluationStack.Push(item?.Value ?? new byte[0]);
                return true;
            }
            return false;
        }

        protected bool StorageContext_AsReadOnly(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (!context.IsReadOnly)
                    context = new StorageContext
                    {
                        ScriptHash = context.ScriptHash,
                        IsReadOnly = true
                    };
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(context));
                return true;
            }
            return false;
        }

        protected bool Contract_GetStorageContext(ExecutionEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                ContractState contract = _interface.GetInterface<ContractState>();
                if (!ContractsCreated.TryGetValue(contract.ScriptHash, out UInt160 created)) return false;
                if (!created.Equals(new UInt160(engine.CurrentContext.ScriptHash))) return false;
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(new StorageContext
                {
                    ScriptHash = contract.ScriptHash,
                    IsReadOnly = false
                }));
                return true;
            }
            return false;
        }

        private bool Contract_Call(ExecutionEngine engine)
        {
            StackItem item0 = engine.CurrentContext.EvaluationStack.Pop();
            ContractState contract;
            if (item0 is InteropInterface<ContractState> _interface)
                contract = _interface;
            else
                contract = Snapshot.Contracts.TryGet(new UInt160(item0.GetByteArray()));
            if (contract is null) return false;
            StackItem item1 = engine.CurrentContext.EvaluationStack.Pop();
            StackItem item2 = engine.CurrentContext.EvaluationStack.Pop();
            ExecutionContext context_new = engine.LoadScript(contract.Script, engine.CurrentContext.ScriptHash, 1);
            context_new.EvaluationStack.Push(item2);
            context_new.EvaluationStack.Push(item1);
            return true;
        }

        protected bool Contract_Destroy(ExecutionEngine engine)
        {
            if (Trigger != TriggerType.Application) return false;
            UInt160 hash = new UInt160(engine.CurrentContext.ScriptHash);
            ContractState contract = Snapshot.Contracts.TryGet(hash);
            if (contract == null) return true;
            Snapshot.Contracts.Delete(hash);
            if (contract.HasStorage)
                foreach (var pair in Snapshot.Storages.Find(hash.ToArray()))
                    Snapshot.Storages.Delete(pair.Key);
            return true;
        }

        private bool PutEx(StorageContext context, byte[] key, byte[] value, StorageFlags flags)
        {
            if (Trigger != TriggerType.Application && Trigger != TriggerType.ApplicationR)
                return false;
            if (key.Length > 1024) return false;
            if (context.IsReadOnly) return false;
            if (!CheckStorageContext(context)) return false;
            StorageKey skey = new StorageKey
            {
                ScriptHash = context.ScriptHash,
                Key = key
            };
            StorageItem item = Snapshot.Storages.GetAndChange(skey, () => new StorageItem());
            if (item.IsConstant) return false;
            item.Value = value;
            item.IsConstant = flags.HasFlag(StorageFlags.Constant);
            return true;
        }

        protected bool Storage_Put(ExecutionEngine engine)
        {
            if (!(engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface))
                return false;
            StorageContext context = _interface.GetInterface<StorageContext>();
            byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            byte[] value = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            return PutEx(context, key, value, StorageFlags.None);
        }

        protected bool Storage_PutEx(ExecutionEngine engine)
        {
            if (!(engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface))
                return false;
            StorageContext context = _interface.GetInterface<StorageContext>();
            byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            byte[] value = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            StorageFlags flags = (StorageFlags)(byte)engine.CurrentContext.EvaluationStack.Pop().GetBigInteger();
            return PutEx(context, key, value, flags);
        }

        protected bool Storage_Delete(ExecutionEngine engine)
        {
            if (Trigger != TriggerType.Application && Trigger != TriggerType.ApplicationR)
                return false;
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (context.IsReadOnly) return false;
                if (!CheckStorageContext(context)) return false;
                StorageKey key = new StorageKey
                {
                    ScriptHash = context.ScriptHash,
                    Key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray()
                };
                if (Snapshot.Storages.TryGet(key)?.IsConstant == true) return false;
                Snapshot.Storages.Delete(key);
                return true;
            }
            return false;
        }
    }
}
