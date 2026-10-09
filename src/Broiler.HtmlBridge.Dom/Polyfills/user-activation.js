// navigator.userActivation (HTML §6.4.3): whether the user has activated the document whose script is
// running, now (transient activation) or ever (sticky activation). Every document shares this one
// navigator, so its one UserActivation answers for whichever document's script asks.
//
// __broilerUserActivation is the host hook, captured into this closure and deleted from the global:
//   navigator          the navigator object, which the attribute is installed on;
//   isActive()         whether the running script's document has transient activation;
//   hasBeenActive()    whether it has ever been activated.
(function () {
    'use strict';

    var host = globalThis.__broilerUserActivation;
    delete globalThis.__broilerUserActivation;

    var UserActivation = function () { throw new TypeError("Failed to construct 'UserActivation': Illegal constructor"); };
    Object.defineProperty(UserActivation, 'name', { value: 'UserActivation', configurable: true });

    var activation = Object.create(UserActivation.prototype);

    var getters = {
        get isActive() {
            if (this !== activation)
                throw new TypeError('Illegal invocation');
            return !!host.isActive();
        },
        get hasBeenActive() {
            if (this !== activation)
                throw new TypeError('Illegal invocation');
            return !!host.hasBeenActive();
        }
    };

    ['hasBeenActive', 'isActive'].forEach(function (name) {
        Object.defineProperty(UserActivation.prototype, name, {
            get: Object.getOwnPropertyDescriptor(getters, name).get, enumerable: true, configurable: true
        });
    });
    Object.defineProperty(UserActivation.prototype, Symbol.toStringTag, { value: 'UserActivation', configurable: true });
    Object.defineProperty(globalThis, 'UserActivation', { value: UserActivation, writable: true, enumerable: false, configurable: true });

    var Nav = typeof Navigator !== 'undefined' ? Navigator : (typeof globalThis !== 'undefined' ? globalThis.Navigator : null);
    var target = (Nav && Nav.prototype) || host.navigator;
    Object.defineProperty(target, 'userActivation', {
        get: Object.getOwnPropertyDescriptor({ get userActivation() {
            if (this !== host.navigator)
                throw new TypeError("Failed to read the 'userActivation' property from 'Navigator': Illegal invocation");
            return activation;
        } }, 'userActivation').get,
        enumerable: true,
        configurable: true
    });
})();
