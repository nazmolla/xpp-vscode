// The server's xpp/bridge actions (MetaModelBridgeServer.DispatchOnMainThread plus where_used, list_tags and tag_references
// in McpStdioServer). scripts/check-bridge-actions.js keeps this list and the server in step.
export const BridgeActions = [
    'create_object', 'read_object', 'read_object_by_path', 'update_object', 'find_object', 'list_objects', 'find_references',
    'get_model_info', 'list_models', 'read_label', 'create_label', 'add_to_project', 'list_project_items', 'remove_from_project',
    'select_project', 'delete_object', 'table_erd', 'incoming_relations', 'type_members', 'label_file_entries', 'save_label',
    'delete_label', 'build', 'get_environment', 'validate_object', 'get_object_type_schema', 'get_form_layout', 'get_form_tree',
    'get_table_fields', 'get_element_tree', 'get_element_properties', 'set_element_property', 'get_node_types', 'add_element_node',
    'delete_element_node', 'rename_element_node', 'duplicate_element_node', 'move_element_node', 'duplicate_object', 'drop_on_node',
    'drop_on_property', 'get_element_method', 'set_element_method', 'get_overrides', 'get_model', 'create_model', 'update_model',
    'list_packages', 'dump_element', 'export_project', 'inspect_project_package', 'import_project',
    'where_used', 'list_tags', 'tag_references',
] as const;

export type BridgeAction = typeof BridgeActions[number];

/** What every bridge action returns; the other members depend on the action. */
export interface BridgeResult {
    success: boolean;
    message?: string;
    [member: string]: any;
}
