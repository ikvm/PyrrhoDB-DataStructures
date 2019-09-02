using System;
using Pyrrho.Level2;
using Pyrrho.Level4;
using Pyrrho.Common;
using System.Text;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland

namespace Pyrrho.Level3
{
    /// <summary>
    /// A Level 3 Method definition (a subclass of Procedure)
    /// Immutable
    /// </summary>
    internal class Method : Procedure
    {
        internal const long
            TypeDef = -170, // UDType
            MethodType = -171; // PMethod.MethodType
        /// <summary>
        /// The owning type definition
        /// </summary>
		public UDType udType => (UDType)mem[TypeDef];
        /// <summary>
        /// The method type (constructor etc)
        /// </summary>
		public PMethod.MethodType methodType => (PMethod.MethodType)mem[MethodType];
        /// <summary>
        /// Constructor: A new level 3 method from a level 2 method
        /// </summary>
        /// <param name="m">The level 2 method</param>
        /// <param name="definer">the definer</param>
        /// <param name="owner">the owner</param>
        /// <param name="rs">the accessing roles</param>
        public Method(PMethod m, Sqlx create, Database db)
            : base(m, db, true, create, BTree<long, object>.Empty
                  + (TypeDef, m.typedefpos) + (MethodType, m.methodType))
        { }
        public Method(long defpos, BTree<long, object> m) : base(defpos, m) { }
        public static Method operator+(Method m,(long,object)x)
        {
            return new Method(m.defpos, m.mem + x);
        }
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            sb.Append(" UDType="); sb.Append(udType);
            sb.Append(" MethodType="); sb.Append(methodType);
            return sb.ToString();
        }
        /// <summary>
        /// Execute a Method
        /// </summary>
        /// <param name="cx">The context</param>
        /// <param name="db">The database</param>
        /// <param name="dt">The return type</param>
        /// <param name="ut">The owning object type</param>
        /// <param name="sce">The source object instance (null for constructor)</param>
        /// <param name="n">The method name</param>
        /// <param name="actIns">The actual parameter list</param>
        /// <returns>The return value</returns>
        public TypedValue Exec(Transaction tr, Context cx, SqlValue var, BList<SqlValue> actIns)
        {
            TypedValue r;
            var a = cx.GetActivation();
            var au = new Context(cx,tr.role, tr.user);
            var bd = body;
            var ut = udType;
            var targ = var.Eval(tr, au);
            var act = new CalledActivation(tr, au, this, ut);
            var acts = new TypedValue[(int)actIns.Count];
            for (int i = 0; i < actIns.Count; i++)
                acts[i] = actIns[i].Eval(tr, cx);
            for (int i = 0; i < actIns.Count; i++)
                act.values+=(ins[i].defpos, acts[i]);
            if (methodType != PMethod.MethodType.Constructor)
                for (int i = 0; i < ut.Length; i++)
                {
                    var se = ut.columns[i];
                    act.values += (se.defpos,cx.values[se.defpos]);
                }
            act.proc.body.Obey(tr,cx);
            r = act.ret;
            for (int i = 0; i < ins.Count; i++)
            {
                var p = ins[i];
                if (cx is Activation ac && (p.paramMode == Sqlx.INOUT || p.paramMode == Sqlx.OUT))
                    acts[i] = act.values[p.defpos];
                if (p.paramMode == Sqlx.RESULT)
                    r = act.values[p.defpos];
            }
            if (methodType == PMethod.MethodType.Constructor)
            {
                var ks = new TypedValue[ut.Length];
                for (int i = 0; i < ut.Length; i++)
                    ks[i] = act.values[ut.columns[i].defpos];
                r = new TRow(ut, ks);
            }
            for (int i = 0; i < ins.Count; i++)
            {
                var p = ins[i];
                if (cx is Activation ac && (p.paramMode == Sqlx.INOUT || p.paramMode == Sqlx.OUT))
                    ac.values+=(actIns[i].defpos, acts[i]);
            } 
            return r;
        }
        /// <summary>
        /// test for depndenccy during drop/rename
        /// </summary>
        /// <param name="t">the drop/rename transaction</param>
        /// <returns>the nature of the depedency</returns>
        public override Sqlx Dependent(Transaction t,Context cx)
        {
            if (t.refObj.defpos == udType.defpos)
                return Sqlx.DROP;
            return base.Dependent(t,cx);
        }
    }

}
