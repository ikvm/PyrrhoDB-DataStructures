using System;
using Pyrrho.Level1;
using Pyrrho.Level3;
using Pyrrho.Level4;
using Pyrrho.Common;

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
	/// A Delete entry notes a base table record to delete
	/// </summary>
	internal class Delete : Physical
	{
        /// <summary>
        /// The reference deletion constraint allows us to check if the record is referred to by a foreign key
        /// </summary>
        public ReferenceDeletionConstraint delC = null;
        public long delpos;
        public long tabledefpos;
        public override long Dependent(Writer wr)
        {
            var dp = wr.Fix(delpos);
            if (!Committed(wr,dp)) return dp;
            if (!Committed(wr,tabledefpos)) return tabledefpos;
            return -1;
        }
        /// <summary>
        /// Constructor: a new Delete request from the engine
        /// </summary>
        /// <param name="rc">The defining position of the record</param>
        /// <param name="tb">The local database</param>
        public Delete(TableRow rw, long u, Transaction tr)
            : base(Type.Delete, u, tr)
		{
            tabledefpos = rw.tabledefpos;
            delpos = rw.defpos;
		}
        /// <summary>
        /// Constructor: a new Delete request from the buffer
        /// </summary>
        /// <param name="bp">the buffer</param>
        /// <param name="pos">a defining position</param>
		public Delete(Reader rdr) : base(Type.Delete, rdr) { }
        protected Delete(Delete x, Writer wr) : base(x, wr)
        {
            tabledefpos = wr.Fix(x.tabledefpos);
            delpos = wr.Fix(x.delpos);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Delete(this, wr);
        }
        /// <summary>
        /// The affected record
        /// </summary>
		public override long Affects
		{
			get
			{
				return delpos;
			}
		}
        /// <summary>
        /// Serialise the Delete to the PhysBase
        /// </summary>
        /// <param name="r">Reclocation of position information</param>
        public override void Serialise(Writer wr)
		{
            wr.PutLong(wr.Fix(delpos));
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise the Delete from the buffer
        /// </summary>
        /// <param name="buf">The buffer</param>
        public override void Deserialise(Reader rdr)
        {
            var dp = rdr.GetLong();
            base.Deserialise(rdr);
            delpos= dp;
        }
        /// <summary>
        /// A readable version of the Delete
        /// </summary>
        /// <returns>The string representation</returns>
		public override string ToString()
        {
            return "Delete Record ["+Pos(delpos)+"]";
        }
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch (that.type)
            {
                case Type.Delete:
                    return (((Delete)that).delpos == delpos) ? ppos : -1;
                case Type.Update:
                    return (((Update)that)._defpos == delpos) ? ppos : -1;
            }
            return -1;
        }

        internal override Database Install(Database db, Role ro, long p)
        {
            var tb = db.schemaRole.objects[tabledefpos] as Table;
            var delRow = tb.tableRows[delpos];
            for (var b=tb.indexes.First();b!=null;b=b.Next())
            {
                var ix = b.value();
                var inf = ix.rows.info;
                var key = delRow.MakeKey(ix);
                ix -= key;
                if (ix.rows == null)
                    ix+=(Index.Tree,new MTree(inf));
                tb += (Table.Indexes, tb.indexes + (b.key(), ix));
            }
            tb += (Table.Rows, tb.tableRows - delpos);
            return db+ (db.schemaRole, tb, p);
        }
    }
}
