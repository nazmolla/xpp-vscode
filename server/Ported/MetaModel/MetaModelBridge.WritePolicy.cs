namespace XppVsCode.Server.MetaModel
{
    public sealed partial class MetaModelBridge : IWritePolicy
    {
        public string EditDenial(string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName) || !ObjectExists(objectType, objectName))
                return null;
            string model = ModelOf(objectType, objectName);
            return IsCustomModel(model) ? null
                : objectType + " '" + objectName + "' belongs to " + (model ?? "a standard model")
                    + ", which you can't change. Create an extension in your model instead.";
        }

        public string TargetModelDenial(string modelName)
        {
            string model = ResolveTargetModelName(modelName);
            if (model == null)
                return null;
            return IsCustomModel(model) ? null
                : "Model '" + model + "' is not one of your custom models. Create objects in your own model.";
        }
    }
}
