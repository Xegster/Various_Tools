//var cookies = require('../../javascript/cookies.js');
const state = {
	
}

const getters = {
	exampleGetter: (state, getters) => (param1, param2) => {
		return { ...state, ...getters, param1, param2 };
	},

}
const actions = {
	exampleAction: ({ commit, state, getters, dispatch }, payload) => {
		//commit for mutations
		commit('exampleMutation', payload);
		commit('exampleMutation', { ...state, ...getters });
		//dispatch actions, even across modules
		dispatch('module2/exampleMutation', [], { root: true });

	},
	examplePromise: ({ commit, state, getters, dispatch }, payload) => {
		new Promise((resolve, reject) => {
			//do stuff
			//commit for mutations
			commit('exampleMutation', payload);
			commit('exampleMutation', { ...state, ...getters });
			//dispatch actions, even across modules
			dispatch('module2/exampleMutation', [], { root: true });
			//if success
			resolve(0);
			//if error 
			reject(0);

		});
	}
}

const mutations = {
	exampleMutation: (state, payload) => {
		state = payload;
	},

}

export default {
	namespaced: true,
	state,
	getters,
	actions,
	mutations
}



