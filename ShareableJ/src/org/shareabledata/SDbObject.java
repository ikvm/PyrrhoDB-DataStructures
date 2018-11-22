/*
 * To change this license header, choose License Headers in Project Properties.
 * To change this template file, choose Tools | Templates
 * and open the template in the editor.
 */
package org.shareabledata;

/**
 *
 * @author Malcolm
 */
public class SDbObject extends Serialisable {
    /// <summary>
    /// For database objects such as STable, we will want to record 
    /// a unique id based on the actual position in the transaction log,
    /// so the Get and Commit methods will capture the appropriate 
    /// file positions in AStream – this is why the Commit method 
    /// needs to create a new instance of the Serialisable. 
    /// The uid will initially belong to the Transaction. 
    /// Once committed the uid will become the position in the AStream file.
    /// </summary>

    public final long uid;
    /// <summary>
    /// We will allow clients to define SColumns etc, with an impossible uid
    /// </summary>
    /// <param name="t"></param>

    protected SDbObject(int t) {
        super(t);
        uid = -1;
    }
    /// <summary>
    /// For system tables and columns, with negative uids
    /// </summary>
    /// <param name="t"></param>
    /// <param name="u"></param>

    protected SDbObject(int t, long u) {
        super(t);
        uid = u;
    }

    /// <summary>
    /// For a new database object we set the transaction-based uid
    /// </summary>
    /// <param name="t"></param>
    /// <param name="tr"></param>
    protected SDbObject(int t, STransaction tr) {
        super(t);
        uid = tr.uid + 1;
    }
    /// <summary>
    /// A modified database obejct will keep its uid
    /// </summary>
    /// <param name="s"></param>

    protected SDbObject(SDbObject s) {
        super(s.type);
        uid = s.uid;
    }
    /// <summary>
    /// A database object got from the file will have
    /// its uid given by the position it is read from
    /// </summary>
    /// <param name="t"></param>
    /// <param name="f"></param>

    protected SDbObject(int t, Reader f) {
        super(t);
        uid = (f instanceof SocketReader)?-1:f.getPosition()-1;
    }

    protected SDbObject(SDbObject s, AStream f) throws Exception {
        super(s.type);
        if (s.uid < STransaction._uid) {
            throw new Exception("Internal error - misplaced database object");
        }
        uid = f.pos();
        f.uids = f.uids.Add(s.uid, uid);
        f.WriteByte((byte) s.type);
    }

    void Check(Boolean committed) throws Exception {
        if (committed != uid < STransaction._uid) {
            throw new Exception("Internal error - Commited check fails");
        }
    }

    String Uid() {
        return STransaction.Uid(uid);
    }

    public String toString() {
        return Types.ToString(type) + "[" + Uid() + "] ";
    }
}
