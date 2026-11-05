import { doc, ElementSpec, lower } from './xpp';

/**
 * The elements of a custom service (JSON/SOAP endpoint at /api/services/<group>/<service>/<operation>): request and
 * response contracts, the service class, the service, a service group that deploys it, and a privilege for the operation.
 */
export function serviceElements(base: string, label: string): ElementSpec[] {
    const request = `${base}Request`, response = `${base}Response`, service = `${base}Service`, group = `${base}ServiceGroup`;
    const parm = (type: string, name: string, summary: string) =>
        `${doc(summary)}[DataMemberAttribute('${name}')]\npublic ${type} parm${name}(${type} _${lower(name)} = ${lower(name)})\n{\n    ${lower(name)} = _${lower(name)};\n    return ${lower(name)};\n}`;
    return [
        {
            objectType: 'AxClass', objectName: request, body: {
                declaration: `${doc(`Request of the ${service}.process operation.`)}[DataContractAttribute]\nclass ${request}\n{\n    DataAreaId dataAreaId;\n    Description description;\n}`,
                methods: [parm('DataAreaId', 'DataAreaId', 'The company the request is for.'), parm('Description', 'Description', 'The request data; replace with your fields.')],
            },
        },
        {
            objectType: 'AxClass', objectName: response, body: {
                declaration: `${doc(`Response of the ${service}.process operation.`)}[DataContractAttribute]\nclass ${response}\n{\n    boolean success;\n    str message;\n}`,
                methods: [
                    `${doc('Whether the request succeeded.')}[DataMemberAttribute('Success')]\npublic boolean parmSuccess(boolean _success = success)\n{\n    success = _success;\n    return success;\n}`,
                    `${doc('What happened, for the caller.')}[DataMemberAttribute('Message')]\npublic str parmMessage(str _message = message)\n{\n    message = _message;\n    return message;\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: service, body: {
                declaration: `${doc(`${label}: the operations of the ${service} service.`)}class ${service}\n{\n}`,
                methods: [`${doc('Handles one request in the request\'s company.', '/// <param name="_request">The request.</param>\n/// <returns>The result.</returns>\n')}public ${response} process(${request} _request)\n{\n`
                    + `    ${response} response = new ${response}();\n\n    try\n    {\n        changecompany(_request.parmDataAreaId() ? _request.parmDataAreaId() : curExt())\n        {\n`
                    + `            ttsbegin;\n            // Do the work for _request here.\n            ttscommit;\n        }\n        response.parmSuccess(true);\n    }\n`
                    + `    catch (Exception::Error)\n    {\n        response.parmSuccess(false);\n        response.parmMessage(infolog.text(infologLine()));\n    }\n    return response;\n}`],
            },
        },
        {
            objectType: 'AxService', objectName: service, body: {
                typedMetadata: { Class: service, ExternalName: service, Description: label, ServiceOperations: [{ Name: 'process', Method: 'process' }] },
            },
        },
        { objectType: 'AxServiceGroup', objectName: group, body: { typedMetadata: { Description: label, AutoDeploy: 'Yes', Services: [{ Name: service, Service: service }] } } },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}Invoke`, body: {
                properties: { Label: label },
                entryPoints: [{ name: `${service}process`, objectType: 'ServiceOperation', objectName: `${service}.process`, grant: 'Invoke' }],
            },
        },
    ];
}

