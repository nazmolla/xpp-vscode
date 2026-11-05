namespace XppVsCode.Server.MetaModel
{
    /// <summary>Who may change what: only objects and models of the user's custom models are written.</summary>
    public interface IWritePolicy
    {
        /// <summary>Why the object can't be changed, or null when it can (or doesn't exist: the action reports that).</summary>
        string EditDenial(string objectType, string objectName);

        /// <summary>Why objects can't be created in the model (null = the active project's model), or null when they can.</summary>
        string TargetModelDenial(string modelName);
    }
}
