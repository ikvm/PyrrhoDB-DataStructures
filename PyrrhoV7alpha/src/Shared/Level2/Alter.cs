using System;
using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code 
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland
namespace Pyrrho.Level2
{
    /// <summary>
    /// Alter records in the database are for modifying TableColumn information
    /// (There are other records for altering domains etc)
    /// Care: the Alter classes do not inherit from each other, but from the different PColumn classes
    /// </summary>
    internal class Alter : PColumn
    {
        /// <summary>
        /// Alter is obsolete: use Alter3.
        /// We remember the previous entry for this table column (the most recent Alter or the definition)
        /// </summary>
        public long defpos;
        /// <summary>
        /// Constructor: a new Alter record from the disk file
        /// </summary>
        /// <param name="bp">The buffer being read</param>
        /// <param name="pos">The defining position</param>
        public Alter(Reader rdr) : base(Type.Alter, rdr) { }
        protected Alter(Alter x, Writer wr) : base(x, wr)
        {
            defpos = wr.Fix(x.defpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Alter(this, wr);
        }
        /// <summary>
        /// Deserialise the Alter from the disk file buffer
        /// </summary>
        /// <param name="buf">The buffer</param>
        public override void Deserialise(Reader rdr)
        {
            var prev = rdr.GetLong();
            defpos = ((DBObject)rdr.db.role.objects[prev]).defpos;
            base.Deserialise(rdr);
        }
        /// <summary>
        /// Provide a string version of the Alter
        /// </summary>
        /// <returns>a string representation</returns>
        public override string ToString()
        {
            return "Alter TableColumn  [" + Pos(defpos) + "] to " + base.ToString();
        }
    }
    /// <summary>
    /// Alter records in the database are for modifying base column information
    /// (There are other records for altering domains etc)
    /// </summary>
    internal class Alter2 : PColumn2
    {
        /// <summary>
        /// Alter2 is obsolete - use Alter3
        /// We remember the previous entry for this ident (the most recent Alter or the definition)
        /// </summary>
        public long defpos;
        /// <summary>
        /// Constructor: a new Alter2 record from the disk
        /// </summary>
        /// <param name="bp">The buffer being read</param>
        /// <param name="pos">The defining position</param>
        public Alter2(Reader rdr) : base(Type.Alter2, rdr) { }
        protected Alter2(Alter2 x, Writer wr) : base(x, wr)
        {
            defpos = wr.Fix(x.defpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Alter2(this, wr);
        }
        /// <summary>
        /// Deserialise the Alter from the disk file buffer
        /// </summary>
        /// <param name="buf"></param>
        public override void Deserialise(Reader rdr)
        {
            var prev = rdr.GetLong();
            defpos = ((DBObject)rdr.db.role.objects[prev]).defpos;
            base.Deserialise(rdr);
        }
    }
	/// <summary>
	/// Alter records in the database are for modifying base column information
    /// (There are other records for altering domains etc)
	/// </summary>
	internal class Alter3 : PColumn3
	{
		public long defpos;
        /// <summary>
        /// Constructor: a new Alter record from the Parser
        /// </summary>
        /// <param name="co">The ident defining position</param>
        /// <param name="nm">The (new) table column name</param>
        /// <param name="sq">The (new) table column position (0,1,..)</param>
        /// <param name="tb">The table defining position</param>
        /// <param name="dm">The (new) domain defining position</param>
        /// <param name="ds">The (new) default string</param>
        /// <param name="ua">The update assignment rule</param>
        /// <param name="nn">The (new) setting for NOT NULL</param>
        /// <param name="ge">The (new) setting for GENERATED ALWAYS</param>
        /// <param name="db">The local database</param>
        public Alter3(long co, string nm, int sq, long tb, long dm, 
            string ds, string ua, bool nn, GenerationRule ge, long u, Transaction tr) :
            base(Type.Alter3, tb, nm, sq, dm, ds, ua, nn, ge, u, tr)
		{
            defpos = co;
		}
        /// <summary>
        /// Constructor: a new Alter record from the disk
        /// </summary>
        /// <param name="bp">The buffer being read</param>
        /// <param name="pos">The defining position</param>
		public Alter3(Reader rdr) : base(Type.Alter3, rdr) { }
        protected Alter3(Alter3 x, Writer wr) : base(x, wr)
        {
            defpos = wr.Fix(x.defpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Alter3(this, wr);
        }
        /// <summary>
        /// Serialise the Alter to the PhysBase
        /// </summary>
        /// <param name="r">Relocate some Position references</param>
        public override void Serialise(Writer wr)
		{
            wr.PutLong(defpos);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise the Alter from the disk file buffer
        /// </summary>
        /// <param name="rdr"></param>
        public override void Deserialise(Reader rdr) 
		{ 
			var previous = rdr.GetLong();
            defpos = ((DBObject)rdr.db.role.objects[previous]).defpos;
			base.Deserialise(rdr);
		}
        /// <summary>
        /// Provide a string version of the Alter
        /// </summary>
        /// <returns></returns>
		public override string ToString() 
		{ 
			return "Alter3 TableColumn  ["+defpos+"] "+base.ToString(); 
		}
        /// <summary>
        /// ReadCheck is used by ReadConstraint
        /// </summary>
        /// <param name="pos">A Position to check</param>
        /// <returns>Whether read conflict has occurred</returns>
		public override DBException ReadCheck(long pos)
		{
			return (pos==defpos)?new DBException("40078",pos).Mix() :null;
		}
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch (that.type)
            {
                case Type.Alter3:
                    {
                        var a = (Alter3)that;
                        return (defpos == a.defpos ||
                            (tabledefpos == a.tabledefpos && name.CompareTo(a.name) == 0)) ?
                            ppos : -1;
                    }
                case Type.Alter2:
                    {
                        var a = (Alter2)that;
                        return (defpos == a.defpos ||
                            (tabledefpos == a.tabledefpos && name.CompareTo(a.name) == 0)) ?
                            ppos : -1;
                    }
                case Type.Alter:
                    {
                        var a = (Alter)that;
                        return (defpos == a.defpos ||
                            (tabledefpos == a.tabledefpos && name.CompareTo(a.name) == 0)) ?
                            ppos : -1;
                    }
                case Type.PColumn3:
                case Type.PColumn2:
                case Type.PColumn:
                    {
                        var a = (PColumn)that;
                        return (tabledefpos == a.tabledefpos && name.CompareTo(a.name) == 0) ?
                            ppos : -1;
                    }
                case Type.Record3:
                case Type.Record2:
                case Type.Record1:
                case Type.Record:
                    {
                        var r = (Record)that;
                        return (tabledefpos == r.tabledefpos && r.fields.Contains(defpos)) ?
                            ppos : -1;
                    }
                case Type.Update1:
                case Type.Update:
                    {
                        var r = (Update)that;
                        return (tabledefpos == r.tabledefpos && r.fields.Contains(defpos)) ?
                            ppos : -1;
                    }
                case Type.Drop:
                    {
                        var d = (Drop)that;
                        return (tabledefpos == d.delpos || defpos == d.delpos) ?
                            ppos : -1;
                    }
                case Type.PIndex:
                    {
                        var c = (PIndex)that;
                        if (tabledefpos==c.tabledefpos)
                            for (int j = 0; j < c.columns.Count; j++)
                                if (c.columns[j] == defpos 
                                    || c.columns[j] == -defpos)
                                    return ppos;
                        return -1;
                    }
                case Type.Grant:
                    {
                        var g = (Grant)that;
                        return (tabledefpos == g.obj || defpos == g.obj) ? ppos : -1;
                    }
                case Type.PCheck:
                    {
                        var c = (PCheck)that;
                        return (tabledefpos == c.ckobjdefpos || defpos == c.ckobjdefpos) ?
                            ppos : -1;
                    }
            }
            return base.Conflicts(db, tr, that);
        }
	}
}